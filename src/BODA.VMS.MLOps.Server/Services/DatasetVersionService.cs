using System.IO.Compression;
using BODA.VMS.MLOps.Contracts;
using BODA.VMS.MLOps.Contracts.Datasets;
using BODA.VMS.MLOps.Core.Domain;
using BODA.VMS.MLOps.Server.Auth;
using BODA.VMS.MLOps.Server.Data;
using BODA.VMS.MLOps.Server.Data.Entities;
using BODA.VMS.MLOps.Server.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace BODA.VMS.MLOps.Server.Services;

/// <summary>
/// 데이터셋 버전 조회와 zip 업로드.
/// 스냅샷을 만드는 일은 <see cref="DatasetSnapshotService"/> 가 맡고, 여기는 밖에서 만든 zip 을 받는 길이다
/// (WPF 도구가 내보낸 데이터셋을 그대로 학습에 쓰는 경우).
/// </summary>
public sealed class DatasetVersionService(
    MlopsDbContext db, IArtifactStorage storage, AuditService audit, IOptions<MlopsOptions> options, TimeProvider clock)
{
    public static readonly string[] Formats = ["yolo", "imagefolder", "mvtec", "ppocr", "coco"];

    public async Task<DatasetVersionDto> UploadAsync(DatasetVersionUploadMeta meta, Stream zip, CurrentUser user, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(meta.Name))
            throw ApiException.BadRequest(ErrorCodes.Validation, "데이터셋 이름은 필수입니다.");
        var format = (meta.ExportFormat ?? "").Trim().ToLowerInvariant();
        if (!Formats.Contains(format))
            throw ApiException.BadRequest(ErrorCodes.Validation, $"exportFormat 은 {string.Join("|", Formats)} 중 하나여야 합니다.");

        var temp = await TempFileWriter.WriteAsync(storage, zip, options.Value.MaxDatasetBytes, ".zip", ct);
        try
        {
            ValidateZip(temp.TempPath, options.Value.MaxDatasetBytes);
            var id = Guid.NewGuid();
            var key = StorageKeys.Dataset(id);
            await storage.CommitTempAsync(temp.TempPath, key, ct);

            var dv = new DatasetVersion
            {
                Id = id, Name = meta.Name.Trim(), TaskType = meta.TaskType, ExportFormat = format,
                // 밖에서 만들어 온 zip 은 내용 목록을 알 수 없다. 그래서 신원도 바이트 해시로 둔다 —
                // 이때만 두 값이 같고, 스냅샷에서는 서로 다른 것을 가리킨다.
                ManifestHash = temp.Sha256, ExportSha256 = temp.Sha256,
                StorageKey = key, SizeBytes = temp.SizeBytes, ImageCount = meta.ImageCount,
                ClassesJson = Mapping.ToJson(meta.Classes ?? []), CreatedBy = user.Name,
                CreatedAt = clock.GetUtcNow().UtcDateTime, Source = DatasetVersionSource.Upload,
            };
            db.DatasetVersions.Add(dv);
            audit.Record(AuditService.Dataset, "VersionUploaded", user.Name, id.ToString(),
                new { dv.Name, dv.TaskType, format, dv.ManifestHash, dv.SizeBytes });
            await db.SaveChangesAsync(ct);
            return dv.ToDto();
        }
        finally { TempFileWriter.TryDelete(temp.TempPath); }
    }

    public async Task<List<DatasetVersionDto>> ListAsync(Guid? datasetId, CancellationToken ct)
    {
        var q = db.DatasetVersions.AsNoTracking().AsQueryable();
        if (datasetId is not null) q = q.Where(d => d.DatasetId == datasetId);
        return (await q.OrderByDescending(d => d.CreatedAt).ToListAsync(ct)).Select(d => d.ToDto()).ToList();
    }

    public async Task<DatasetVersionDto> GetAsync(Guid id, CancellationToken ct) =>
        (await db.DatasetVersions.AsNoTracking().FirstOrDefaultAsync(d => d.Id == id, ct)
         ?? throw ApiException.NotFound("데이터셋 버전")).ToDto();

    /// <summary>버전을 지운다. 학습 작업이 참조하고 있으면 막는다 — 재현성 레코드가 가리키는 대상이기 때문이다.</summary>
    public async Task DeleteAsync(Guid id, CurrentUser user, CancellationToken ct)
    {
        var version = await db.DatasetVersions.FirstOrDefaultAsync(d => d.Id == id, ct) ?? throw ApiException.NotFound("데이터셋 버전");
        if (await db.TrainingJobs.AnyAsync(j => j.DatasetVersionId == id, ct))
            throw ApiException.Conflict(ErrorCodes.Validation, "이 버전으로 만든 학습 작업이 있어 지울 수 없습니다.");

        db.DatasetVersions.Remove(version);
        audit.Record(AuditService.Dataset, "VersionDeleted", user.Name, id.ToString(), new { version.Name });
        await db.SaveChangesAsync(ct);

        if (version.StorageKey is { } key) await storage.DeleteAsync(key, ct);
        if (version.ManifestKey is { } manifest) await storage.DeleteAsync(manifest, ct);
    }

    /// <summary>
    /// zip 구조·경로 탈출(..)·절대 경로 항목 거부 (Phase 3 §8) + 해제 후 총 크기 상한.
    /// 여기서 걸러야 워커가 압축 폭탄을 받아 디스크를 채우는 일이 없다 (워커도 해제하며 실제 바이트를 다시 센다).
    /// </summary>
    private static void ValidateZip(string path, long maxUncompressedBytes)
    {
        try
        {
            using var archive = ZipFile.OpenRead(path);
            if (archive.Entries.Count == 0)
                throw ApiException.BadRequest(ErrorCodes.Validation, "빈 zip 입니다.");
            long total = 0;
            foreach (var e in archive.Entries)
            {
                var n = e.FullName.Replace('\\', '/');
                if (n.StartsWith('/') || n.Contains("../") || n.Contains("/..") || n == ".." || Path.IsPathRooted(n))
                    throw ApiException.BadRequest(ErrorCodes.Validation, $"zip 항목 경로가 허용되지 않습니다: {e.FullName}");
                total += e.Length;
                if (total > maxUncompressedBytes)
                    throw ApiException.TooLarge($"zip 해제 크기가 상한({maxUncompressedBytes / (1024 * 1024)}MB)을 넘습니다.");
            }
        }
        catch (InvalidDataException)
        {
            throw ApiException.BadRequest(ErrorCodes.UnsupportedFormat, "zip 파일이 아닙니다.");
        }
    }
}
