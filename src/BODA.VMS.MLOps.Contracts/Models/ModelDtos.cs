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

/// <summary>
/// 모델 돌려 보기 (개발 문서 §5.5 3번) — 고른 사진에 그 버전을 돌린다. 승격하는 사람이 지표 숫자만 보고 누르지 않게.
/// 결과는 <see cref="TryModelResultDto.ScoreFloor"/> 까지 담아 보내므로 신뢰도 문턱은 화면이 바로 바꾼다.
/// </summary>
public sealed record TryModelRequest(Guid[] ImageIds);

/// <summary>후보 하나. 좌표는 0~1 정규화 (x, y 는 왼쪽 위)</summary>
public sealed record TryDetectionDto(int ClassIndex, string ClassName, double Score, double X, double Y, double W, double H);

/// <param name="MaxScore">가장 높은 후보 점수. 후보가 없으면 null — 바닥값(ScoreFloor) 아래라 모른다는 뜻이다.</param>
public sealed record TryImageResultDto(Guid ImageId, IReadOnlyList<TryDetectionDto> Detections, double? MaxScore, string? Error = null);

/// <param name="NmsIou">서버가 YOLO 에 건 NMS 문턱. 화면은 이보다 낮은 값으로만 더 거를 수 있다. D-FINE 은 NMS 를 쓰지 않아 null.</param>
public sealed record TryModelResultDto(
    Guid ModelVersionId, ModelFormat Format, string[] Classes, double ScoreFloor, double? NmsIou,
    IReadOnlyList<TryImageResultDto> Images, long ElapsedMs);
