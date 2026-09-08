using BODA.VMS.MLOps.Core.Domain;

namespace BODA.VMS.MLOps.Contracts.Datasets;

// ───────────── 데이터셋 ─────────────

public sealed record CreateDatasetRequest(string Name, TaskType TaskType, string[] Classes, string? Description = null);

public sealed record UpdateDatasetRequest(string? Name = null, string? Description = null, bool? IsArchived = null);

public sealed record DatasetDto(
    Guid Id, string Name, TaskType TaskType, string[] Classes, string? Description,
    string CreatedBy, DateTime CreatedAt, bool IsArchived, DatasetStatsDto? Stats);

public sealed record DatasetStatsDto(
    int ImageCount, int Labeled, int Reviewed, int Unlabeled, int AnnotationCount,
    int TrainCount, int ValCount, int TestCount, Dictionary<string, int> PerClass);

// ───────────── 이미지 풀 ─────────────

public sealed record ImageDto(
    Guid Id, string Sha256, string FileName, string ContentType, int Width, int Height, long SizeBytes,
    ImageSource Source, string? LineId, string? InspectionId, string[] Tags, string? PerceptualHash,
    DateTime? CapturedAt, string CreatedBy, DateTime CreatedAt,
    string ThumbnailUrl, string ViewUrl, string OriginalUrl,
    /// <summary>데이터셋 범위로 조회했을 때만 채워진다</summary>
    LabelStatus? LabelStatus = null, DatasetSplit? Split = null, int? AnnotationCount = null, string? LockedBy = null);

public sealed record ImagePageDto(IReadOnlyList<ImageDto> Items, int Total, int Skip, int Take);

/// <summary>업로드 결과. 같은 파일이면 created=false 로 기존 레코드를 돌려준다.</summary>
public sealed record ImageUploadResultDto(ImageDto Image, bool Created, string? NearDuplicateOf);

public sealed record ImageUploadBatchDto(IReadOnlyList<ImageUploadResultDto> Results, int Created, int Duplicates, string[] Errors);

public sealed record TagImagesRequest(Guid[] ImageIds, string[]? Add = null, string[]? Remove = null);

public sealed record DeleteImagesRequest(Guid[] ImageIds);

public sealed record DuplicateGroupDto(IReadOnlyList<ImageDto> Images);

// ───────────── 데이터셋 구성 ─────────────

public sealed record AddImagesRequest(Guid[] ImageIds, DatasetSplit Split = DatasetSplit.Train);

public sealed record SetSplitRequest(Guid[] ImageIds, DatasetSplit Split);

public sealed record AutoSplitRequest(double TrainRatio = 0.8, double ValRatio = 0.2);

// ───────────── 라벨링 ─────────────

/// <summary>좌표는 0~1 정규화. 도형에 따라 쓰는 필드가 다르다.</summary>
public sealed record AnnotationDto(
    AnnotationShape Shape,
    string ClassName,
    double? X = null, double? Y = null, double? W = null, double? H = null,
    double[][]? Points = null,
    string? Text = null);

public sealed record ImageLabelsDto(
    Guid DatasetId, Guid ImageId, IReadOnlyList<AnnotationDto> Annotations,
    LabelStatus Status, string? LockedBy, DateTime? LockExpiresAt, bool LockedByMe,
    string? LabeledBy, DateTime? LabeledAt, string? ReviewedBy, DateTime? ReviewedAt);

public sealed record SaveLabelsRequest(IReadOnlyList<AnnotationDto> Annotations, bool MarkLabeled = false);

public sealed record ReviewRequest(bool Approved);

public sealed record NextImageDto(Guid? ImageId);

// ───────────── 스냅샷 ─────────────

public sealed record CreateSnapshotRequest(
    string? Name = null,
    /// <summary>라벨이 없는 이미지도 담는다. 검출에서 배경 샘플로 쓸 때만 켠다.</summary>
    bool IncludeUnlabeled = false,
    /// <summary>검토를 마친 이미지만 담는다.</summary>
    bool ReviewedOnly = false);
