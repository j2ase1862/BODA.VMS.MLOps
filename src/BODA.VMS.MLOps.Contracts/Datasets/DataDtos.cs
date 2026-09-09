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
    LabelStatus? LabelStatus = null, DatasetSplit? Split = null, int? AnnotationCount = null, string? LockedBy = null,
    /// <summary>
    /// 격자에서 썸네일 위에 라벨을 겹쳐 그리기 위한 것. 데이터셋 범위 조회에서만 채워진다.
    /// 좌표가 정규화라 썸네일 크기에 그대로 얹을 수 있다.
    /// </summary>
    IReadOnlyList<AnnotationDto>? Annotations = null,
    /// <summary>
    /// 흐림·노출 지표. 사람이 걸러 볼 후보를 좁히는 데 쓴다 — 이 값으로 자동으로 버리지 않는다.
    /// 이 값이 생기기 전에 올라온 이미지는 null 이다.
    /// </summary>
    ImageQualityDto? Quality = null);

/// <summary>
/// 이미지 한 장의 품질 지표.
///
/// <para>
/// <see cref="Sharpness"/> 는 축소본(긴 변 2048 이하) 기준 라플라시안 분산이라 <b>절대값에 뜻이 없다</b>.
/// 무늬가 촘촘한 부품은 흐려도 크고, 매끈한 도장면은 또렷해도 작다. 같은 라인·같은 배율의
/// 사진들 사이에서 상대적으로 낮은 것을 찾는 데만 쓴다.
/// </para>
/// </summary>
public sealed record ImageQualityDto(
    double Sharpness,
    double MeanLuma,
    double ClippedDarkRatio,
    double ClippedBrightRatio)
{
    /// <summary>날아간 화소 비율 (어두운 쪽 + 밝은 쪽)</summary>
    public double ClippedRatio => ClippedDarkRatio + ClippedBrightRatio;
}

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

// ───────────── Active Learning ─────────────

/// <summary>후보 모델이 한 장에 대해 내놓은 것. 사람이 손댄 이미지에는 채우지 않는다.</summary>
public sealed record PrefillImageDto(
    Guid ImageId,
    IReadOnlyList<AnnotationDto> Annotations,
    /// <summary>
    /// 이 이미지가 얼마나 애매한지 (클수록 애매하다). 라벨링 큐가 이 값이 큰 것부터 내보낸다.
    /// 척도는 모델마다 다르므로 서버는 값을 해석하지 않고 순서에만 쓴다.
    /// </summary>
    double? Uncertainty = null);

public sealed record PrefillRequest(IReadOnlyList<PrefillImageDto> Images);

public sealed record PrefillResultDto(int Filled, int Received);

/// <summary>후보 모델로 미라벨 이미지를 훑어 초기 라벨과 불확실도를 채운다.</summary>
public sealed record PrelabelRequest(
    Guid ModelVersionId,
    /// <summary>이보다 낮은 확신도는 버린다. 낮추면 더 많이 붙지만 사람이 지울 것도 늘어난다.</summary>
    double Confidence = 0.25,
    /// <summary>한 번에 훑을 이미지 수. 남으면 다시 부르면 된다.</summary>
    int MaxImages = 200);

public sealed record PrelabelResultDto(
    /// <summary>대상으로 고른 미라벨 이미지 수</summary>
    int Considered,
    /// <summary>실제로 추론을 마친 수</summary>
    int Inferred,
    /// <summary>라벨을 채운 이미지 수 (사람이 손댄 것은 제외된다)</summary>
    int Filled,
    /// <summary>읽지 못해 건너뛴 수</summary>
    int Skipped,
    /// <summary>붙인 라벨 개수 합계</summary>
    int Annotations,
    string? Message = null);

// ───────────── 스냅샷 ─────────────

public sealed record CreateSnapshotRequest(
    string? Name = null,
    /// <summary>라벨이 없는 이미지도 담는다. 검출에서 배경 샘플로 쓸 때만 켠다.</summary>
    bool IncludeUnlabeled = false,
    /// <summary>검토를 마친 이미지만 담는다.</summary>
    bool ReviewedOnly = false);
