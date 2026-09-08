using System.Text.Json;
using BODA.VMS.MLOps.Contracts;
using BODA.VMS.MLOps.Contracts.Models;
using BODA.VMS.MLOps.Contracts.Training;
using BODA.VMS.MLOps.Core.Domain;
using BODA.VMS.MLOps.Core.Hashing;
using BODA.VMS.MLOps.Core.Onnx;
using BODA.VMS.MLOps.Core.Training;
using BODA.VMS.MLOps.Server.Auth;
using BODA.VMS.MLOps.Server.Data;
using BODA.VMS.MLOps.Server.Data.Entities;
using BODA.VMS.MLOps.Server.Hubs;
using BODA.VMS.MLOps.Server.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace BODA.VMS.MLOps.Server.Services;

/// <summary>
/// 학습 작업 큐·상태 머신·워커 프로토콜 (Phase 3 §3, §4, §5, §7).
/// 상태는 서버가 소유한다: 워커는 ack/progress/artifacts/finish 로 보고만 하고, 전이 규칙은 <see cref="TrainingJobStateMachine"/> 이 검증한다.
/// </summary>
public sealed class TrainingJobService(
    MlopsDbContext db, IArtifactStorage storage, AuditService audit, IMlopsNotifier notifier, ScriptManifestService scripts,
    ModelRegistryService registry, IOptions<MlopsOptions> options, TimeProvider clock, ILogger<TrainingJobService> logger)
{
    private static readonly SemaphoreSlim AssignLock = new(1, 1);
    private static readonly TrainingJobState[] ActiveStates =
        [TrainingJobState.Assigned, TrainingJobState.Preparing, TrainingJobState.Running, TrainingJobState.Exporting, TrainingJobState.Uploading];

    private DateTime Now => clock.GetUtcNow().UtcDateTime;

    // ───────────── 생성·조회·취소 ─────────────

    public async Task<TrainingJobDto> CreateAsync(CreateTrainingJobRequest req, CurrentUser user, CancellationToken ct)
    {
        var dataset = await db.DatasetVersions.AsNoTracking().FirstOrDefaultAsync(d => d.Id == req.DatasetVersionId, ct)
                      ?? throw ApiException.NotFound("데이터셋 버전");
        var model = await db.Models.AsNoTracking().FirstOrDefaultAsync(m => m.Id == req.ModelId, ct)
                    ?? throw ApiException.NotFound("모델");

        var taskType = req.Script.TaskType();
        if (dataset.TaskType != taskType)
            throw ApiException.BadRequest(ErrorCodes.TaskTypeMismatch, $"데이터셋 작업 유형 {dataset.TaskType} 은(는) 스크립트 {req.Script}({taskType}) 와 맞지 않습니다.");
        if (model.TaskType != taskType)
            throw ApiException.BadRequest(ErrorCodes.TaskTypeMismatch, $"모델 작업 유형 {model.TaskType} 은(는) 스크립트 {req.Script}({taskType}) 와 맞지 않습니다.");
        var expectedFormat = req.Script.DatasetExportFormat();
        if (!dataset.ExportFormat.Equals(expectedFormat, StringComparison.OrdinalIgnoreCase))
            throw ApiException.BadRequest(ErrorCodes.TaskTypeMismatch, $"데이터셋 내보내기 형식 {dataset.ExportFormat} ≠ 스크립트 요구 형식 {expectedFormat}");

        var hyperparams = HyperparamWhitelist.Validate(req.Script, req.Hyperparams, out var errors)
                          ?? throw ApiException.BadRequest(ErrorCodes.InvalidHyperparams, "허용되지 않은 하이퍼파라미터가 있습니다.", errors);

        if (req.Script == TrainingScript.TrainYolo && string.IsNullOrWhiteSpace(req.License))
            throw ApiException.BadRequest(ErrorCodes.LicenseRequired, "train_yolo(AGPL-3.0) 작업은 Enterprise License 확인 문자열(license)이 필요합니다.");

        if (!string.IsNullOrWhiteSpace(req.PretrainedRef) && !await db.PretrainedAssets.AnyAsync(a => a.Ref == req.PretrainedRef, ct))
            throw ApiException.NotFound($"사전학습 자산 '{req.PretrainedRef}'");

        var scriptName = req.Script.FileName();
        if (scripts.GetHash(scriptName) is null)
            throw ApiException.BadRequest(ErrorCodes.Validation, $"서버에 스크립트가 없습니다: {scriptName}");

        var dedup = DedupKey(req.DatasetVersionId, req.ModelId, req.Script, req.Backbone, req.PretrainedRef, req.Seed, hyperparams);
        if (!req.Force && await db.TrainingJobs.AnyAsync(j => j.DedupKey == dedup && j.State == TrainingJobState.Succeeded, ct))
            throw ApiException.Conflict(ErrorCodes.DuplicateJob, "같은 데이터셋 버전·설정으로 성공한 작업이 있습니다. force=true 로 강제 실행할 수 있습니다.");

        var job = new TrainingJob
        {
            Id = Guid.NewGuid(), ProjectId = req.ProjectId, DatasetVersionId = dataset.Id, ModelId = model.Id, TaskType = taskType,
            Script = req.Script, Backbone = req.Backbone, PretrainedRef = string.IsNullOrWhiteSpace(req.PretrainedRef) ? null : req.PretrainedRef,
            HyperparamsJson = Mapping.ToJson(hyperparams), Seed = req.Seed, State = TrainingJobState.Queued, Priority = req.Priority,
            MaxAttempts = options.Value.MaxAttempts, CreatedBy = user.Name, CreatedAt = Now, DedupKey = dedup, License = req.License,
        };
        db.TrainingJobs.Add(job);
        audit.Record(AuditService.Training, "JobCreated", user.Name, job.Id.ToString(),
            new { job.DatasetVersionId, job.ModelId, job.Script, job.Backbone, job.PretrainedRef, hyperparams, job.Seed, job.Priority });
        await db.SaveChangesAsync(ct);
        var dto = job.ToDto();
        await notifier.JobQueued(dto);
        return dto;
    }

    public async Task<List<TrainingJobDto>> ListAsync(TrainingJobState? state, Guid? modelId, int take, CancellationToken ct)
    {
        var q = db.TrainingJobs.AsNoTracking().AsQueryable();
        if (state is not null) q = q.Where(j => j.State == state);
        if (modelId is not null) q = q.Where(j => j.ModelId == modelId);
        return (await q.OrderByDescending(j => j.CreatedAt).Take(Math.Clamp(take, 1, 1000)).ToListAsync(ct)).Select(j => j.ToDto()).ToList();
    }

    public async Task<TrainingJobDto> GetAsync(Guid id, CancellationToken ct) =>
        (await db.TrainingJobs.AsNoTracking().FirstOrDefaultAsync(j => j.Id == id, ct) ?? throw ApiException.NotFound("학습 작업")).ToDto();

    public async Task<TrainingJobDto> CancelAsync(Guid id, CancelJobRequest req, CurrentUser user, CancellationToken ct)
    {
        var job = await db.TrainingJobs.FirstOrDefaultAsync(j => j.Id == id, ct) ?? throw ApiException.NotFound("학습 작업");
        if (TrainingJobStateMachine.IsTerminal(job.State))
            throw ApiException.Conflict(ErrorCodes.InvalidJobTransition, $"이미 종료된 작업입니다 ({job.State}).");
        if (!string.Equals(job.CreatedBy, user.Name, StringComparison.OrdinalIgnoreCase) && !user.IsAdmin)
            throw ApiException.Forbidden("다른 사람의 작업 취소는 Admin 권한이 필요합니다.");

        job.CancelRequested = true;
        job.CancelReason = req.Reason;
        if (job.State == TrainingJobState.Queued)
        {
            job.State = TrainingJobState.Cancelled;
            job.FinishedAt = Now;
            audit.Record(AuditService.Training, "JobCancelled", user.Name, job.Id.ToString(), new { req.Reason });
            await db.SaveChangesAsync(ct);
            var done = job.ToDto();
            await notifier.JobDone(done);
            return done;
        }
        audit.Record(AuditService.Training, "JobCancelRequested", user.Name, job.Id.ToString(), new { req.Reason, job.State });
        await db.SaveChangesAsync(ct);
        var dto = job.ToDto();
        await notifier.JobProgress(dto);
        return dto;
    }

    /// <summary>같은 설정으로 재실행 (Phase 3 §9)</summary>
    public async Task<TrainingJobDto> RetryAsync(Guid id, CurrentUser user, CancellationToken ct)
    {
        var job = await db.TrainingJobs.AsNoTracking().FirstOrDefaultAsync(j => j.Id == id, ct) ?? throw ApiException.NotFound("학습 작업");
        var hp = Mapping.Json(job.HyperparamsJson, new Dictionary<string, string>());
        var req = new CreateTrainingJobRequest(job.DatasetVersionId, job.ModelId, job.Script, job.Backbone, job.PretrainedRef,
            JsonSerializer.SerializeToElement(hp, MlopsJson.Options), job.Seed, job.Priority, job.ProjectId, Force: true, License: job.License);
        return await CreateAsync(req, user, ct);
    }

    public async Task<JobLogPage> LogsAsync(Guid id, long fromSeq, int take, CancellationToken ct)
    {
        if (!await db.TrainingJobs.AnyAsync(j => j.Id == id, ct)) throw ApiException.NotFound("학습 작업");
        var lines = await db.JobLogChunks.AsNoTracking().Where(c => c.JobId == id && c.Seq > fromSeq)
            .OrderBy(c => c.Seq).Take(Math.Clamp(take, 1, 5000)).ToListAsync(ct);
        return new JobLogPage(id, fromSeq, lines.Select(l => l.ToDto()).ToList(), lines.Count == 0 ? fromSeq : lines[^1].Seq);
    }

    public async Task<List<JobArtifactDto>> ArtifactsAsync(Guid id, CancellationToken ct)
    {
        if (!await db.TrainingJobs.AnyAsync(j => j.Id == id, ct)) throw ApiException.NotFound("학습 작업");
        return (await db.JobArtifacts.AsNoTracking().Where(a => a.JobId == id).OrderBy(a => a.Kind).ToListAsync(ct))
            .Select(a => new JobArtifactDto(a.Kind, a.FileName, a.Sha256, a.SizeBytes, ArtifactUrl(id, a.Kind))).ToList();
    }

    public async Task<(Stream Stream, JobArtifact Artifact)> OpenArtifactAsync(Guid id, JobArtifactKind kind, CancellationToken ct)
    {
        var a = await db.JobArtifacts.AsNoTracking().Where(x => x.JobId == id && x.Kind == kind).OrderByDescending(x => x.CreatedAt).FirstOrDefaultAsync(ct)
                ?? throw ApiException.NotFound("아티팩트");
        if (!storage.Exists(a.StorageKey)) throw ApiException.NotFound("아티팩트 파일");
        return (storage.OpenRead(a.StorageKey), a);
    }

    // ───────────── 워커 프로토콜 ─────────────

    /// <summary>long-poll: waitSeconds 동안 1초 간격으로 배정을 시도한다. 없으면 null(204).</summary>
    public async Task<JobAssignment?> NextAsync(Guid workerId, int? waitSeconds, CancellationToken ct)
    {
        var wait = TimeSpan.FromSeconds(Math.Clamp(waitSeconds ?? options.Value.PollIntervalSec, 0, 120));
        var deadline = clock.GetUtcNow() + wait;
        while (true)
        {
            var assignment = await TryAssignAsync(workerId, ct);
            if (assignment is not null) return assignment;
            if (clock.GetUtcNow() >= deadline || ct.IsCancellationRequested) return null;
            await Task.Delay(TimeSpan.FromSeconds(1), ct);
        }
    }

    private async Task<JobAssignment?> TryAssignAsync(Guid workerId, CancellationToken ct)
    {
        await AssignLock.WaitAsync(ct);
        try
        {
            // long-poll 은 한 요청 안에서 이 메서드를 수십 번 부른다. 같은 scoped DbContext 가 추적 중인 엔티티를
            // 그대로 돌려주므로, 비우지 않으면 워커의 비활성 여부·디스크 여유가 첫 회전 값에 고정된다
            // (관리자가 중간에 비활성해도 작업이 배정되고 Disabled 가 Busy 로 덮어써진다).
            db.ChangeTracker.Clear();

            var worker = await db.Workers.FirstOrDefaultAsync(w => w.Id == workerId, ct) ?? throw ApiException.NotFound("워커");
            if (worker.AdminDisabled || worker.Status == WorkerStatus.Disabled)
                throw new ApiException(403, ErrorCodes.WorkerDisabled, worker.DisabledReason ?? "워커가 비활성 상태입니다.");

            // 재접속: 이미 이 워커에 배정된 활성 작업이 있으면 같은 배정을 돌려준다 (멱등)
            var current = await db.TrainingJobs.FirstOrDefaultAsync(j => j.WorkerId == worker.Id && ActiveStates.Contains(j.State), ct);
            if (current is not null) return await BuildAssignmentAsync(current, ct);

            var taskTypes = Mapping.Json(worker.TaskTypesJson, Array.Empty<TaskType>());
            var candidates = await db.TrainingJobs.AsNoTracking().Where(j => j.State == TrainingJobState.Queued)
                .OrderByDescending(j => j.Priority).ThenBy(j => j.CreatedAt).Take(50).ToListAsync(ct);
            if (candidates.Count == 0) return null;

            var datasetIds = candidates.Select(c => c.DatasetVersionId).Distinct().ToList();
            var sizes = await db.DatasetVersions.AsNoTracking().Where(d => datasetIds.Contains(d.Id))
                .ToDictionaryAsync(d => d.Id, d => d.SizeBytes, ct);

            var picked = candidates.FirstOrDefault(j =>
                taskTypes.Contains(j.TaskType)
                && (worker.DiskFreeGB <= 0 || sizes.GetValueOrDefault(j.DatasetVersionId) / 1e9 <= worker.DiskFreeGB));
            if (picked is null) return null;

            // AsNoTracking 으로 골랐으니 수정용으로 다시 읽는다. 그 사이 다른 워커가 가져갔으면 이번 회전은 건너뛴다.
            var job = await db.TrainingJobs.FirstOrDefaultAsync(j => j.Id == picked.Id && j.State == TrainingJobState.Queued, ct);
            if (job is null) return null;

            var scriptHash = scripts.GetHash(job.Script.FileName());
            if (scriptHash is null)
            {
                job.State = TrainingJobState.Failed;
                job.FailureKind = FailureKinds.PrepareError;
                job.Error = $"서버에 스크립트가 없습니다: {job.Script.FileName()}";
                job.FinishedAt = Now;
                await db.SaveChangesAsync(ct);
                await notifier.JobDone(job.ToDto());
                return null;
            }

            job.State = TrainingJobState.Assigned;
            job.WorkerId = worker.Id;
            job.AssignedAt = Now;
            job.AckedAt = null;
            job.Attempt++;
            job.ScriptSha256 = scriptHash;
            job.Progress = 0;
            job.CurrentEpoch = 0;
            job.Error = null;
            job.FailureKind = null;
            worker.Status = WorkerStatus.Busy;
            worker.CurrentJobId = job.Id;
            await db.SaveChangesAsync(ct);
            logger.LogInformation("작업 {JobId} → 워커 {Worker} 배정 (attempt {Attempt})", job.Id, worker.Name, job.Attempt);
            await notifier.JobProgress(job.ToDto());
            return await BuildAssignmentAsync(job, ct);
        }
        finally
        {
            AssignLock.Release();
        }
    }

    private async Task<JobAssignment> BuildAssignmentAsync(TrainingJob job, CancellationToken ct)
    {
        var dataset = await db.DatasetVersions.AsNoTracking().FirstOrDefaultAsync(d => d.Id == job.DatasetVersionId, ct)
                      ?? throw ApiException.NotFound("데이터셋 버전");
        var scriptName = job.Script.FileName();
        var hash = job.ScriptSha256 ?? scripts.GetHash(scriptName) ?? "";

        var pretrained = new List<PretrainedFileRef>();
        if (job.PretrainedRef is not null)
        {
            var asset = await db.PretrainedAssets.AsNoTracking().FirstOrDefaultAsync(a => a.Ref == job.PretrainedRef, ct);
            if (asset is not null)
                foreach (var f in Mapping.Json(asset.FilesJson, new List<PretrainedMirrorService.FileEntry>()))
                    pretrained.Add(new PretrainedFileRef(asset.Ref, f.FileName, f.Sha256, f.SizeBytes, PretrainedMirrorService.FileUrl(asset.Ref, f.FileName)));
        }

        return new JobAssignment(job.ToDto(),
            $"/api/dataset-versions/{dataset.Id}/export?format={dataset.ExportFormat}", dataset.ManifestHash, dataset.SizeBytes, dataset.ExportFormat,
            pretrained, scriptName, hash, $"/api/workers/scripts/{scriptName}");
    }

    private async Task<TrainingJob> LoadOwnedAsync(Guid jobId, Guid workerId, CancellationToken ct)
    {
        var job = await db.TrainingJobs.FirstOrDefaultAsync(j => j.Id == jobId, ct) ?? throw ApiException.NotFound("학습 작업");
        if (job.WorkerId != workerId)
            throw ApiException.Forbidden("이 워커에 배정된 작업이 아닙니다.");
        return job;
    }

    /// <summary>
    /// 워커 보고 저장. 감독자가 같은 행을 재큐해 Stamp 가 바뀌었으면 409 로 알린다.
    /// 워커는 이를 client error 로 보고 로컬 실행을 중단하므로, 두 워커가 같은 작업을 이어서 도는 상황이 생기지 않는다.
    /// </summary>
    private async Task SaveWorkerReportAsync(CancellationToken ct)
    {
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException ex)
        {
            logger.LogWarning(ex, "작업 상태가 다른 곳에서 바뀌어 워커 보고를 반영하지 못했습니다");
            throw ApiException.Conflict(ErrorCodes.InvalidJobTransition,
                "작업 상태가 서버에서 변경되었습니다 (재큐·취소 등). 이 작업은 중단하세요.");
        }
    }

    public async Task<TrainingJobDto> AckAsync(Guid jobId, Guid workerId, CancellationToken ct)
    {
        var job = await LoadOwnedAsync(jobId, workerId, ct);
        if (job.State == TrainingJobState.Preparing) return job.ToDto(); // 멱등
        if (!TrainingJobStateMachine.CanTransition(job.State, TrainingJobState.Preparing))
            throw ApiException.Conflict(ErrorCodes.InvalidJobTransition, $"ack 불가: {job.State}");
        job.State = TrainingJobState.Preparing;
        job.AckedAt = Now;
        job.LastProgressAt = Now;
        await SaveWorkerReportAsync(ct);
        var dto = job.ToDto();
        await notifier.JobProgress(dto);
        return dto;
    }

    public async Task<(TrainingJobDto Job, bool CancelRequested)> ProgressAsync(Guid jobId, Guid workerId, ProgressReport report, CancellationToken ct)
    {
        var job = await LoadOwnedAsync(jobId, workerId, ct);
        if (TrainingJobStateMachine.IsTerminal(job.State))
            throw ApiException.Conflict(ErrorCodes.InvalidJobTransition, $"종료된 작업입니다 ({job.State}).");

        if (report.State is { } target && target != job.State)
        {
            if (!TrainingJobStateMachine.IsWorkerReportable(target) || !TrainingJobStateMachine.CanTransition(job.State, target))
                throw ApiException.Conflict(ErrorCodes.InvalidJobTransition, $"{job.State} → {target} 전이는 허용되지 않습니다.");
            job.State = target;
            if (target == TrainingJobState.Running && job.StartedAt is null) job.StartedAt = Now;
        }

        job.Progress = Math.Clamp(report.Progress, 0, 100);
        job.CurrentEpoch = Math.Max(0, report.Epoch);
        job.TotalEpochs = Math.Max(job.TotalEpochs, report.TotalEpochs);
        if (report.Loss is not null) job.LastLoss = report.Loss;
        if (report.Metric is not null) job.LastMetric = report.Metric;
        job.LastProgressAt = Now;

        var lines = new List<JobLogLineDto>();
        if (report.LogChunks is { Count: > 0 })
        {
            foreach (var chunk in report.LogChunks)
            {
                job.LogSeq++;
                var text = chunk.Text.Length > 4000 ? chunk.Text[..4000] : chunk.Text;
                var at = chunk.At == default ? Now : chunk.At;
                db.JobLogChunks.Add(new JobLogChunk { JobId = job.Id, Seq = job.LogSeq, Level = chunk.Level, Text = text, At = at });
                lines.Add(new JobLogLineDto(job.LogSeq, chunk.Level, text, at));
            }
        }
        // LogSeq 는 이 행에서 증가시키므로 Stamp 검사가 (JobId, Seq) 중복 삽입도 함께 막는다
        await SaveWorkerReportAsync(ct);

        var dto = job.ToDto();
        await notifier.JobProgress(dto);
        if (lines.Count > 0) await notifier.JobLog(job.Id, lines);
        return (dto, job.CancelRequested);
    }

    /// <summary>아티팩트 저장 → best.onnx 는 Phase 1 검증 파이프라인으로 ModelVersion(Candidate) 생성 (Phase 3 §5.3)</summary>
    public async Task<ArtifactsResponse> UploadArtifactsAsync(Guid jobId, Guid workerId, IFormCollection form, CurrentUser user, CancellationToken ct)
    {
        var job = await LoadOwnedAsync(jobId, workerId, ct);
        // 학습을 시작하지도 않은 작업이 아티팩트만 올려 Succeeded 로 건너뛰지 못하게 한다
        if (job.State is not (TrainingJobState.Running or TrainingJobState.Exporting or TrainingJobState.Uploading))
            throw ApiException.Conflict(ErrorCodes.InvalidJobTransition,
                $"아티팩트는 Running 이후에만 올릴 수 있습니다 (현재 {job.State}).");
        if (form.Files.Count == 0)
            throw ApiException.BadRequest(ErrorCodes.Validation, "아티팩트 파일이 없습니다 (onnx|metrics|curve|train_info|log).");

        var warnings = new List<string>();
        var dtos = new List<JobArtifactDto>();
        string? onnxKey = null;
        Dictionary<string, double> metrics = new();
        JsonElement? trainInfo = null;

        foreach (var file in form.Files)
        {
            var kind = KindFromField(file.Name)
                       ?? throw ApiException.BadRequest(ErrorCodes.Validation, $"알 수 없는 아티팩트 필드: {file.Name} (onnx|metrics|curve|train_info|log)");
            var fileName = StorageKeys.SafeName(string.IsNullOrWhiteSpace(file.FileName) ? DefaultName(kind) : file.FileName);
            var key = StorageKeys.JobFile(job.Id, fileName);

            await using (var s = file.OpenReadStream())
            {
                var temp = await TempFileWriter.WriteAsync(storage, s, MaxBytesFor(kind, options.Value), ".bin", ct);
                try
                {
                    await storage.DeleteAsync(key, ct);
                    await storage.CommitTempAsync(temp.TempPath, key, ct);
                }
                finally { TempFileWriter.TryDelete(temp.TempPath); }

                var old = await db.JobArtifacts.Where(a => a.JobId == job.Id && a.Kind == kind).ToListAsync(ct);
                db.JobArtifacts.RemoveRange(old);
                db.JobArtifacts.Add(new JobArtifact
                {
                    Id = Guid.NewGuid(), JobId = job.Id, Kind = kind, FileName = fileName, StorageKey = key,
                    Sha256 = temp.Sha256, SizeBytes = temp.SizeBytes, CreatedAt = Now,
                });
                dtos.Add(new JobArtifactDto(kind, fileName, temp.Sha256, temp.SizeBytes, ArtifactUrl(job.Id, kind)));
            }

            switch (kind)
            {
                case JobArtifactKind.Onnx: onnxKey = key; break;
                case JobArtifactKind.Metrics: metrics = ReadNumericJson(storage.LocalPath(key), warnings); break;
                case JobArtifactKind.TrainInfo: trainInfo = ReadJson(storage.LocalPath(key), warnings); break;
            }
        }
        await db.SaveChangesAsync(ct);

        Guid? versionId = job.ResultModelVersionId;
        if (onnxKey is not null)
        {
            var (classes, inputSize, extraMetrics) = ExtractTrainInfo(trainInfo);
            foreach (var (k, v) in extraMetrics) metrics.TryAdd(k, v);
            var meta = new VersionUploadMeta(classes, inputSize, job.License,
                $"TrainingJob {job.Id:N} · {job.Script} · dataset {job.DatasetVersionId:N} · seed {job.Seed}", metrics);

            await using var onnxStream = storage.OpenRead(onnxKey);
            var (vdto, created) = await registry.UploadVersionAsync(job.ModelId, onnxStream, meta, VersionSource.TrainingJob, job.Id, user, ct);
            versionId = vdto.Id;
            job.ResultModelVersionId = versionId;
            if (!created) warnings.Add($"같은 SHA-256 의 버전(v{vdto.Number})이 이미 있어 그 버전에 연결했습니다.");
            warnings.AddRange(vdto.Warnings);
        }

        if (TrainingJobStateMachine.CanTransition(job.State, TrainingJobState.Uploading))
            job.State = TrainingJobState.Uploading;
        job.LastProgressAt = Now;
        await SaveWorkerReportAsync(ct);
        await notifier.JobProgress(job.ToDto());
        return new ArtifactsResponse(job.Id, versionId, dtos, warnings.ToArray());
    }

    public async Task<TrainingJobDto> FinishAsync(Guid jobId, Guid workerId, FinishJobRequest req, CancellationToken ct)
    {
        var job = await LoadOwnedAsync(jobId, workerId, ct);
        if (req.State is not (TrainingJobState.Succeeded or TrainingJobState.Failed or TrainingJobState.Cancelled))
            throw ApiException.BadRequest(ErrorCodes.Validation, "finish 상태는 Succeeded|Failed|Cancelled 여야 합니다.");
        if (TrainingJobStateMachine.IsTerminal(job.State))
        {
            if (job.State == req.State) return job.ToDto(); // 재시도 멱등
            throw ApiException.Conflict(ErrorCodes.InvalidJobTransition, $"이미 {job.State} 로 종료된 작업입니다.");
        }

        // 전이는 반드시 상태 머신을 거친다. Succeeded 는 Uploading 에서만 나올 수 있으므로,
        // Running/Exporting 에서 바로 finish 한 경우에만 Uploading 으로 한 칸 보정한다 (그 보정도 규칙으로 검사).
        if (req.State == TrainingJobState.Succeeded)
        {
            if (job.ResultModelVersionId is null)
                throw ApiException.BadRequest(ErrorCodes.Validation, "best.onnx 아티팩트가 등록되지 않아 Succeeded 로 종료할 수 없습니다.");
            if (job.State != TrainingJobState.Uploading)
            {
                if (!TrainingJobStateMachine.CanTransition(job.State, TrainingJobState.Uploading))
                    throw ApiException.Conflict(ErrorCodes.InvalidJobTransition, $"{job.State} 에서 바로 Succeeded 로 끝낼 수 없습니다.");
                job.State = TrainingJobState.Uploading;
            }
            job.Progress = 100;
        }
        if (!TrainingJobStateMachine.CanTransition(job.State, req.State))
            throw ApiException.Conflict(ErrorCodes.InvalidJobTransition, $"{job.State} → {req.State} 전이는 허용되지 않습니다.");

        job.State = req.State;
        job.FinishedAt = Now;
        job.Error = req.Error;
        job.FailureKind = req.FailureKind ?? (req.State == TrainingJobState.Failed ? FailureKinds.ScriptError : null);
        if (req.Reproducibility is not null) job.ReproducibilityJson = Mapping.ToJson(req.Reproducibility);

        var worker = await db.Workers.FirstOrDefaultAsync(w => w.Id == workerId, ct);
        if (worker is not null && worker.CurrentJobId == job.Id)
        {
            worker.CurrentJobId = null;
            if (!worker.AdminDisabled && worker.Status != WorkerStatus.Disabled) worker.Status = WorkerStatus.Online;
        }

        audit.Record(AuditService.Training, "Job" + req.State, $"worker:{worker?.Name ?? workerId.ToString()}", job.Id.ToString(),
            new { req.State, req.Error, job.FailureKind, job.ResultModelVersionId, job.Attempt });
        await SaveWorkerReportAsync(ct);
        var dto = job.ToDto();
        await notifier.JobDone(dto);
        return dto;
    }

    // ───────────── 감독 ─────────────

    /// <summary>하트비트 소실 → Offline + WorkerLost 재큐, ack 타임아웃 → 재큐 (Phase 3 §3, §4). 조정 건수를 돌려준다.</summary>
    public async Task<int> SuperviseAsync(CancellationToken ct)
    {
        var now = Now;
        var o = options.Value;
        int changes = 0;
        var done = new List<TrainingJobDto>();
        var updated = new List<TrainingJobDto>();

        var lostCut = now.AddSeconds(-o.HeartbeatLostSec);
        var lostWorkers = await db.Workers
            .Where(w => (w.Status == WorkerStatus.Online || w.Status == WorkerStatus.Busy) && (w.LastHeartbeatAt == null || w.LastHeartbeatAt < lostCut))
            .ToListAsync(ct);
        foreach (var w in lostWorkers)
        {
            w.Status = WorkerStatus.Offline;
            changes++;
            var active = await db.TrainingJobs.Where(j => j.WorkerId == w.Id && ActiveStates.Contains(j.State)).ToListAsync(ct);
            foreach (var j in active)
            {
                j.Error = $"워커 '{w.Name}' 하트비트 소실 ({o.HeartbeatLostSec}s)";
                j.FailureKind = FailureKinds.WorkerLost;
                if (j.Attempt < j.MaxAttempts) { Requeue(j); updated.Add(j.ToDto()); }
                else { Fail(j, now); done.Add(j.ToDto()); }
                audit.Record(AuditService.Training, j.State == TrainingJobState.Queued ? "JobRequeued" : "JobFailed", "system", j.Id.ToString(), new { reason = FailureKinds.WorkerLost, w.Name, j.Attempt });
                changes++;
            }
            w.CurrentJobId = null;
            logger.LogWarning("워커 {Worker} 하트비트 소실 → Offline, 활성 작업 {Count}건 처리", w.Name, active.Count);
        }

        var ackCut = now.AddSeconds(-o.AckTimeoutSec);
        var stale = await db.TrainingJobs.Where(j => j.State == TrainingJobState.Assigned && j.AssignedAt < ackCut).ToListAsync(ct);
        foreach (var j in stale)
        {
            var w = j.WorkerId is null ? null : await db.Workers.FirstOrDefaultAsync(x => x.Id == j.WorkerId, ct);
            if (w is not null && w.CurrentJobId == j.Id)
            {
                w.CurrentJobId = null;
                if (w.Status == WorkerStatus.Busy) w.Status = WorkerStatus.Online;
            }
            j.Error = $"워커 ack 타임아웃 ({o.AckTimeoutSec}s)";
            j.FailureKind = FailureKinds.AckTimeout;
            if (j.Attempt < j.MaxAttempts) { Requeue(j); updated.Add(j.ToDto()); }
            else { Fail(j, now); done.Add(j.ToDto()); }
            audit.Record(AuditService.Training, j.State == TrainingJobState.Queued ? "JobRequeued" : "JobFailed", "system", j.Id.ToString(), new { reason = FailureKinds.AckTimeout, j.Attempt });
            changes++;
        }

        if (changes > 0)
        {
            try
            {
                await db.SaveChangesAsync(ct);
            }
            catch (DbUpdateConcurrencyException ex)
            {
                // 워커가 그 사이 ack/progress/finish 를 보냈다. 워커 보고가 이긴다 — 다음 주기에 다시 판단한다.
                logger.LogInformation(ex, "감독 중 작업 상태가 워커 보고로 바뀌어 이번 주기를 건너뜁니다");
                db.ChangeTracker.Clear();
                return 0;
            }
            foreach (var d in updated) await notifier.JobQueued(d);
            foreach (var d in done) await notifier.JobDone(d);
        }
        return changes;
    }

    private static void Requeue(TrainingJob j)
    {
        j.State = TrainingJobState.Queued;
        j.WorkerId = null;
        j.AssignedAt = null;
        j.AckedAt = null;
        j.StartedAt = null;
        j.Progress = 0;
        j.CurrentEpoch = 0;
    }

    private static void Fail(TrainingJob j, DateTime now)
    {
        j.State = TrainingJobState.Failed;
        j.FinishedAt = now;
    }

    // ───────────── 헬퍼 ─────────────

    public static string DedupKey(Guid datasetVersionId, Guid modelId, TrainingScript script, string? backbone, string? pretrainedRef, int seed, IReadOnlyDictionary<string, string> hp)
    {
        var hpText = string.Join(";", hp.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => $"{kv.Key}={kv.Value}"));
        return Sha256Util.HashString($"{datasetVersionId:N}|{modelId:N}|{script}|{backbone}|{pretrainedRef}|{seed}|{hpText}");
    }

    public static string ArtifactUrl(Guid jobId, JobArtifactKind kind) => $"/api/training-jobs/{jobId}/artifacts/{kind.ToString().ToLowerInvariant()}";

    public static JobArtifactKind? KindFromField(string field) => field.Trim().ToLowerInvariant() switch
    {
        "onnx" or "model" or "best.onnx" => JobArtifactKind.Onnx,
        "metrics" or "metrics.json" => JobArtifactKind.Metrics,
        "curve" or "curves" or "curves.png" => JobArtifactKind.Curve,
        "train_info" or "traininfo" or "vms_train_info.json" => JobArtifactKind.TrainInfo,
        "log" or "train.log" => JobArtifactKind.Log,
        _ => null,
    };

    /// <summary>
    /// 아티팩트 종류별 크기 상한. metrics·train_info 는 서버가 File.ReadAllText 로 통째로 읽으므로
    /// ONNX 와 같은 2GB 한도를 주면 워커 하나가 서버 메모리를 고갈시킬 수 있다.
    /// </summary>
    public static long MaxBytesFor(JobArtifactKind kind, MlopsOptions o) => kind switch
    {
        JobArtifactKind.Onnx => o.MaxModelBytes,
        JobArtifactKind.Metrics or JobArtifactKind.TrainInfo => 1L * 1024 * 1024,
        JobArtifactKind.Curve => 32L * 1024 * 1024,
        JobArtifactKind.Log => 64L * 1024 * 1024,
        _ => 1L * 1024 * 1024,
    };

    private static string DefaultName(JobArtifactKind kind) => kind switch
    {
        JobArtifactKind.Onnx => "best.onnx",
        JobArtifactKind.Metrics => "metrics.json",
        JobArtifactKind.Curve => "curves.png",
        JobArtifactKind.TrainInfo => "vms_train_info.json",
        _ => "train.log",
    };

    private static JsonElement? ReadJson(string? path, List<string> warnings)
    {
        if (path is null || !File.Exists(path)) return null;
        try { using var doc = JsonDocument.Parse(File.ReadAllText(path)); return doc.RootElement.Clone(); }
        catch (JsonException ex) { warnings.Add($"JSON 아티팩트 파싱 실패: {ex.Message}"); return null; }
    }

    private static Dictionary<string, double> ReadNumericJson(string? path, List<string> warnings)
    {
        var result = new Dictionary<string, double>();
        var el = ReadJson(path, warnings);
        if (el is null || el.Value.ValueKind != JsonValueKind.Object) return result;
        foreach (var p in el.Value.EnumerateObject())
            if (p.Value.ValueKind == JsonValueKind.Number && p.Value.TryGetDouble(out var d)) result[p.Name] = d;
        return result;
    }

    /// <summary>vms_train_info.json(train_dfine.py): names(dict|list) · imgsz · map50 · val_loss · epochs …</summary>
    private static (string[]? Classes, int? InputSize, Dictionary<string, double> Metrics) ExtractTrainInfo(JsonElement? info)
    {
        string[]? classes = null;
        int? inputSize = null;
        var metrics = new Dictionary<string, double>();
        if (info is null || info.Value.ValueKind != JsonValueKind.Object) return (classes, inputSize, metrics);

        foreach (var p in info.Value.EnumerateObject())
        {
            switch (p.Name)
            {
                case "names":
                    classes = p.Value.ValueKind == JsonValueKind.Array
                        ? p.Value.EnumerateArray().Select(x => x.ToString()).ToArray()
                        : p.Value.ValueKind == JsonValueKind.Object
                            ? p.Value.EnumerateObject().OrderBy(kv => int.TryParse(kv.Name, out var i) ? i : int.MaxValue).Select(kv => kv.Value.ToString()).ToArray()
                            : OnnxModelInspector.ParseNames(p.Value.ToString());
                    break;
                case "imgsz":
                    inputSize = p.Value.ValueKind == JsonValueKind.Number ? p.Value.GetInt32() : OnnxModelInspector.ParseImgsz(p.Value.ToString());
                    break;
                default:
                    if (p.Value.ValueKind == JsonValueKind.Number && p.Value.TryGetDouble(out var d)) metrics[p.Name] = d;
                    break;
            }
        }
        return (classes, inputSize, metrics);
    }
}
