using BODA.VMS.MLOps.Core.Domain;

namespace BODA.VMS.MLOps.Contracts.Models;

public sealed record CreateModelRequest(string Name, TaskType TaskType, string[] Classes, string? Description = null);

public sealed record UpdateModelRequest(string? Name = null, string? Description = null, bool? IsArchived = null);

public sealed record ModelVersionSummary(Guid Id, int Number, ModelStage Stage, ModelFormat Format, string Sha256, DateTime CreatedAt);

public sealed record ModelDto(
    Guid Id, string Name, TaskType TaskType, string[] Classes, string? Description,
    string CreatedBy, DateTime CreatedAt, bool IsArchived,
    ModelVersionSummary? LatestVersion, ModelVersionSummary? ProductionVersion, int VersionCount, int ActiveBindingCount);

/// <summary>multipart 'meta' 파트 (Phase 1 §5) — ONNX 메타데이터가 부족할 때 보충</summary>
public sealed record VersionUploadMeta(
    string[]? Classes = null,
    int? InputSize = null,
    string? License = null,
    string? Notes = null,
    Dictionary<string, double>? Metrics = null);

public sealed record ModelVersionDto(
    Guid Id, Guid ModelId, int Number, string Sha256, long SizeBytes, ModelFormat Format, int? InputSize,
    string[] Classes, Dictionary<string, string> Metadata, Dictionary<string, double> Metrics,
    VersionSource Source, Guid? TrainingJobId, string? License, ModelStage Stage,
    string? StageChangedBy, DateTime? StageChangedAt, string? Notes, string CreatedBy, DateTime CreatedAt,
    string? ValidationStatus, string[] Warnings, IReadOnlyList<StageHistoryDto>? StageHistory = null);

public sealed record StageHistoryDto(ModelStage FromStage, ModelStage ToStage, string ChangedBy, DateTime ChangedAt, string? Reason);

public sealed record PromoteRequest(ModelStage Stage, string? Reason = null, bool ConfirmLicense = false);

/// <summary>라인 PC 의 model:// 참조 해석 응답 (Phase 1 §5 resolve)</summary>
public sealed record ResolveResponse(Guid ModelId, Guid ModelVersionId, int Number, string Sha256, long SizeBytes, ModelStage Stage, string ArtifactUrl);
