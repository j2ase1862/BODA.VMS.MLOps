using System.Text.Json;
using BODA.VMS.MLOps.Core.Domain;

namespace BODA.VMS.MLOps.Contracts.Training;

/// <summary>학습 작업 생성 (Phase 3 §3, §9). 결과 Candidate 는 ModelId 의 Model 계열에 등록된다.</summary>
public sealed record CreateTrainingJobRequest(
    Guid DatasetVersionId,
    Guid ModelId,
    TrainingScript Script,
    string? Backbone = null,
    string? PretrainedRef = null,
    JsonElement? Hyperparams = null,
    int Seed = 0,
    int Priority = 0,
    string? ProjectId = null,
    bool Force = false,
    /// <summary>train_yolo(AGPL) 작업은 Enterprise License 보유 확인 문자열이 필수 — 결과 ModelVersion.License 로 기록</summary>
    string? License = null);

public sealed record TrainingJobDto(
    Guid Id, string? ProjectId, Guid DatasetVersionId, Guid ModelId, TaskType TaskType, TrainingScript Script,
    string? Backbone, string? PretrainedRef, Dictionary<string, string> Hyperparams, int Seed,
    TrainingJobState State, int Priority, Guid? WorkerId, DateTime? AssignedAt, DateTime? StartedAt, DateTime? FinishedAt,
    int Attempt, int MaxAttempts, double Progress, int CurrentEpoch, int TotalEpochs, double? LastLoss, double? LastMetric,
    string? Error, string? FailureKind, Guid? ResultModelVersionId, ReproducibilityRecord? Reproducibility,
    string CreatedBy, DateTime CreatedAt, bool CancelRequested);

/// <summary>워커 long-poll 응답 — 작업 + 실행에 필요한 URL/해시 (Phase 3 §4)</summary>
public sealed record JobAssignment(
    TrainingJobDto Job,
    string DatasetExportUrl,
    string DatasetManifestHash,
    long DatasetSizeBytes,
    string DatasetExportFormat,
    IReadOnlyList<PretrainedFileRef> PretrainedFiles,
    string ScriptName,
    string ScriptSha256,
    string ScriptUrl);

public sealed record PretrainedFileRef(string Ref, string FileName, string Sha256, long SizeBytes, string Url);

public sealed record LogChunkDto(JobLogLevel Level, string Text, DateTime At);

/// <summary>워커 진행률 보고 (최대 1/s 코얼레싱)</summary>
public sealed record ProgressReport(
    TrainingJobState? State,
    double Progress,
    int Epoch,
    int TotalEpochs,
    double? Loss = null,
    double? Metric = null,
    IReadOnlyList<LogChunkDto>? LogChunks = null);

public sealed record ReproducibilityRecord(
    string DatasetManifestHash, string ScriptSha256, Dictionary<string, string> Packages, int Seed,
    string? WorkerName, string? GpuName, string? PythonVersion, Dictionary<string, string> Hyperparams);

public sealed record FinishJobRequest(TrainingJobState State, string? Error = null, string? FailureKind = null, ReproducibilityRecord? Reproducibility = null);

public sealed record ArtifactsResponse(Guid JobId, Guid? ModelVersionId, IReadOnlyList<JobArtifactDto> Artifacts, string[] Warnings);

public sealed record JobArtifactDto(JobArtifactKind Kind, string FileName, string Sha256, long SizeBytes, string Url);

public sealed record JobLogPage(Guid JobId, long FromSeq, IReadOnlyList<JobLogLineDto> Lines, long NextSeq);

public sealed record JobLogLineDto(long Seq, JobLogLevel Level, string Text, DateTime At);

public sealed record CancelJobRequest(string? Reason = null);

/// <summary>실패 종류 (Phase 3 §3): WorkerLost 만 재큐 대상</summary>
public static class FailureKinds
{
    public const string WorkerLost = "WorkerLost";
    public const string ScriptError = "ScriptError";
    public const string Stalled = "Stalled";
    public const string Timeout = "Timeout";
    public const string PrepareError = "PrepareError";
    public const string UploadError = "UploadError";
    public const string AckTimeout = "AckTimeout";
    public const string Oom = "Oom";
}
