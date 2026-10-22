using System.Globalization;
using System.Text;
using BODA.VMS.MLOps.Contracts.Training;
using BODA.VMS.MLOps.Core.Domain;
using BODA.VMS.MLOps.Server.Data;
using BODA.VMS.MLOps.Server.Data.Entities;
using BODA.VMS.MLOps.Server.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace BODA.VMS.MLOps.Server.Services;

/// <summary>
/// 학습 로그 한 벌 — 머리말(작업·모델 계열·데이터셋·설정·결과) + 본문.
/// 화면의 "로그 다운로드" 와 보관 폴더(<see cref="MlopsOptions.LogArchiveDir"/>)가 같은 내용을 쓴다.
///
/// <para>본문은 워커가 올린 train.log 가 있으면 그것(워커 실행 줄 + 스크립트 출력 전부), 없으면 서버 DB 의 로그 줄이다.
/// train.log 가 없는 경우: 아직 도는 중, 워커가 하트비트를 잃음, 0.1.5 전 워커가 실패·취소로 끝냄.</para>
/// </summary>
public sealed class JobLogArchiveService(
    MlopsDbContext db, IArtifactStorage storage, IOptions<MlopsOptions> options, ILogger<JobLogArchiveService> logger)
{
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    public async Task<(string FileName, byte[] Content)> BuildAsync(Guid jobId, CancellationToken ct)
    {
        var job = await db.TrainingJobs.AsNoTracking().FirstOrDefaultAsync(j => j.Id == jobId, ct)
                  ?? throw ApiException.NotFound("학습 작업");
        var modelName = await db.Models.AsNoTracking().Where(m => m.Id == job.ModelId).Select(m => m.Name).FirstOrDefaultAsync(ct);
        var datasetName = await db.DatasetVersions.AsNoTracking().Where(d => d.Id == job.DatasetVersionId).Select(d => d.Name).FirstOrDefaultAsync(ct);
        var workerName = job.WorkerId is null ? null
            : await db.Workers.AsNoTracking().Where(w => w.Id == job.WorkerId).Select(w => w.Name).FirstOrDefaultAsync(ct);
        var logArtifact = await db.JobArtifacts.AsNoTracking()
            .Where(a => a.JobId == jobId && a.Kind == JobArtifactKind.Log)
            .OrderByDescending(a => a.CreatedAt).FirstOrDefaultAsync(ct);
        var fromWorker = logArtifact is not null && storage.Exists(logArtifact.StorageKey);

        using var ms = new MemoryStream();
        var header = new StringBuilder()
            .AppendLine("# BODA VMS MLOps 학습 로그")
            .AppendLine($"# 작업       : {job.Id}")
            .AppendLine($"# 결과       : {StateText(job.State)}{(job.FailureKind is null ? "" : $" ({job.FailureKind})")}")
            .AppendLine(job.Error is null ? "# 오류       : —" : $"# 오류       : {job.Error}")
            .AppendLine($"# 모델 계열  : {modelName ?? "—"} ({job.ModelId})")
            .AppendLine($"# 데이터셋   : {datasetName ?? "—"} ({job.DatasetVersionId})")
            .AppendLine($"# 스크립트   : {job.Script.FileName()} · seed {job.Seed} · 사전학습 {job.PretrainedRef ?? job.Backbone ?? "—"}")
            .AppendLine($"# 하이퍼파라미터 : {Hyperparams(job.HyperparamsJson)}")
            .AppendLine($"# 제출       : {job.CreatedBy} · {Local(job.CreatedAt)}")
            .AppendLine($"# 시작·종료  : {Local(job.StartedAt)} → {Local(job.FinishedAt)}")
            .AppendLine($"# 워커       : {workerName ?? "—"} · 시도 {job.Attempt}/{job.MaxAttempts}")
            .AppendLine($"# 본문       : {(fromWorker ? "워커가 올린 train.log" : "서버에 남은 로그 줄 (워커의 train.log 없음)")}")
            .AppendLine();
        ms.Write(Utf8.GetBytes(header.ToString()));

        if (fromWorker)
        {
            await using var s = storage.OpenRead(logArtifact!.StorageKey);
            await s.CopyToAsync(ms, ct);
        }
        else
        {
            var lines = await db.JobLogChunks.AsNoTracking().Where(c => c.JobId == jobId).OrderBy(c => c.Seq)
                .Select(c => c.Text).ToListAsync(ct);
            foreach (var line in lines) ms.Write(Utf8.GetBytes(line + Environment.NewLine));
        }

        return (FileName(job, modelName), ms.ToArray());
    }

    /// <summary>
    /// 끝난 작업의 로그를 보관 폴더에 남긴다. 폴더가 설정되지 않았으면 아무것도 하지 않는다.
    /// 실패해도 예외를 내지 않는다 — 보관은 덤이고, 작업 종료(워커 보고·감독)를 막으면 안 된다.
    /// </summary>
    public async Task<string?> ArchiveAsync(Guid jobId, CancellationToken ct)
    {
        var dir = options.Value.LogArchiveDir;
        if (string.IsNullOrWhiteSpace(dir)) return null;
        try
        {
            var (fileName, content) = await BuildAsync(jobId, ct);
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, fileName);
            var temp = path + ".tmp";
            await File.WriteAllBytesAsync(temp, content, ct);
            File.Move(temp, path, overwrite: true);
            logger.LogInformation("학습 로그 보관 {Path}", path);
            return path;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "학습 로그 보관 실패 — 작업 {JobId}, 폴더 {Dir}", jobId, dir);
            return null;
        }
    }

    /// <summary>{종료 시각}_{모델 계열}_{결과}_{작업id 앞 8자}.log — 이름순 정렬이 곧 시간순이다</summary>
    public static string FileName(TrainingJob job, string? modelName)
    {
        var at = DateTime.SpecifyKind(job.FinishedAt ?? DateTime.UtcNow, DateTimeKind.Utc).ToLocalTime();
        return $"{at:yyyy-MM-dd_HHmmss}_{SafePart(modelName ?? "model")}_{StateText(job.State)}_{job.Id.ToString("N")[..8]}.log";
    }

    private static string SafePart(string s)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(s.Trim().Select(c => invalid.Contains(c) || char.IsWhiteSpace(c) ? '_' : c).ToArray());
        if (cleaned.Length > 60) cleaned = cleaned[..60];
        return cleaned.Length == 0 ? "model" : cleaned;
    }

    private static string StateText(TrainingJobState s) => s switch
    {
        TrainingJobState.Succeeded => "성공",
        TrainingJobState.Failed => "실패",
        TrainingJobState.Cancelled => "취소",
        _ => "진행중",
    };

    private static string Local(DateTime? utc) =>
        utc is null ? "—" : DateTime.SpecifyKind(utc.Value, DateTimeKind.Utc).ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

    private static string Hyperparams(string json)
    {
        var hp = Mapping.Json(json, new Dictionary<string, string>());
        return hp.Count == 0 ? "(스크립트 기본값)" : string.Join(" ", hp.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => $"{kv.Key}={kv.Value}"));
    }
}
