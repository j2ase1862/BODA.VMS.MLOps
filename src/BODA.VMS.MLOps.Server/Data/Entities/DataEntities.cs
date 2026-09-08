using BODA.VMS.MLOps.Core.Domain;

namespace BODA.VMS.MLOps.Server.Data.Entities;

/// <summary>
/// 라벨링 대상 묶음 (개발 문서 §4). 작업 유형과 클래스 집합은 생성 후 바꾸지 않는다 —
/// 이미 붙은 라벨의 뜻이 달라지기 때문이다.
/// </summary>
public class Dataset
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    public TaskType TaskType { get; set; }
    public string ClassesJson { get; set; } = "[]";
    public string? Description { get; set; }
    public string CreatedBy { get; set; } = "";
    public DateTime CreatedAt { get; set; }
    public bool IsArchived { get; set; }
}

/// <summary>
/// 이미지 풀의 한 장. 데이터셋에 "보내기" 해도 이 레코드는 복제되지 않고
/// <see cref="DatasetImage"/> 로 참조만 걸린다 (개발 문서 §5.2 — 복제 금지).
/// 같은 파일은 SHA-256 으로 한 벌만 저장한다.
/// </summary>
public class Image
{
    public Guid Id { get; set; }
    public string Sha256 { get; set; } = "";
    public string StorageKey { get; set; } = "";
    public string ThumbnailKey { get; set; } = "";
    /// <summary>캔버스가 쓰는 축소본. 원본이 작으면 원본과 같을 수 있다.</summary>
    public string ViewKey { get; set; } = "";
    public string FileName { get; set; } = "";
    public string ContentType { get; set; } = "image/jpeg";
    public int Width { get; set; }
    public int Height { get; set; }
    public long SizeBytes { get; set; }
    public ImageSource Source { get; set; }
    /// <summary>라인 NG 업로드면 어느 라인인지</summary>
    public string? LineId { get; set; }
    /// <summary>생산 이력의 검사 레코드와 잇는 키 (개발 문서 §4)</summary>
    public string? InspectionId { get; set; }
    public string TagsJson { get; set; } = "[]";
    /// <summary>거의 같은 사진을 찾기 위한 dHash (16진 16자리)</summary>
    public string? PerceptualHash { get; set; }
    public DateTime? CapturedAt { get; set; }
    public string CreatedBy { get; set; } = "";
    public DateTime CreatedAt { get; set; }
}

/// <summary>이미지가 어느 데이터셋에 어떤 분할로 들어가 있는지</summary>
public class DatasetImage
{
    public Guid Id { get; set; }
    public Guid DatasetId { get; set; }
    public Guid ImageId { get; set; }
    public DatasetSplit Split { get; set; }
    public string AddedBy { get; set; } = "";
    public DateTime AddedAt { get; set; }
}

/// <summary>
/// 라벨 하나. 같은 이미지라도 데이터셋마다 클래스 집합이 다르므로 (데이터셋, 이미지) 범위로 붙는다.
/// 좌표는 정규화 값이라 원본 해상도가 달라도 유효하다.
/// </summary>
public class Annotation
{
    public Guid Id { get; set; }
    public Guid DatasetId { get; set; }
    public Guid ImageId { get; set; }
    public AnnotationShape Shape { get; set; }
    public string ClassName { get; set; } = "";
    public string PayloadJson { get; set; } = "{}";
    public string CreatedBy { get; set; } = "";
    public DateTime CreatedAt { get; set; }
    public string? UpdatedBy { get; set; }
    public DateTime? UpdatedAt { get; set; }
}

/// <summary>
/// 데이터셋 안에서 이미지 한 장의 라벨 진행 상태와 잠금 (개발 문서 §5.4 — 이미지 단위 잠금·검토 상태).
/// 잠금은 만료 시각으로 관리해, 브라우저가 그냥 닫혀도 다른 사람이 이어받을 수 있다.
/// </summary>
public class ImageLabelState : IConcurrencyStamped
{
    public Guid Id { get; set; }
    public Guid Stamp { get; set; }
    public Guid DatasetId { get; set; }
    public Guid ImageId { get; set; }
    public LabelStatus Status { get; set; }
    public int AnnotationCount { get; set; }
    public string? LockedBy { get; set; }
    public DateTime? LockedAt { get; set; }
    /// <summary>이 시각이 지나면 잠금은 없는 것으로 본다</summary>
    public DateTime? LockExpiresAt { get; set; }
    public string? LabeledBy { get; set; }
    public DateTime? LabeledAt { get; set; }
    public string? ReviewedBy { get; set; }
    public DateTime? ReviewedAt { get; set; }
    /// <summary>Active Learning 이 매긴 불확실도. 큰 값부터 라벨링한다.</summary>
    public double? Uncertainty { get; set; }
}
