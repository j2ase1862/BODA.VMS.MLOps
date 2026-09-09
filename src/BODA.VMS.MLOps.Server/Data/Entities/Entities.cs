using BODA.VMS.MLOps.Core.Domain;

namespace BODA.VMS.MLOps.Server.Data.Entities;

// JSON 컬럼은 문자열로 저장하고 서비스/매핑 계층에서 직렬화한다 (SQLite, EF Core 8).

/// <summary>모델 계열 (Phase 1 §3)</summary>
public class Model
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    public TaskType TaskType { get; set; }
    public string ClassesJson { get; set; } = "[]";
    public string? Description { get; set; }
    public string CreatedBy { get; set; } = "";
    public DateTime CreatedAt { get; set; }
    public bool IsArchived { get; set; }
    public List<ModelVersion> Versions { get; set; } = new();
}

/// <summary>불변 아티팩트 1개 (Phase 1 §3). (ModelId, Number) 유일, (ModelId, Sha256) 유일.</summary>
public class ModelVersion
{
    public Guid Id { get; set; }
    public Guid ModelId { get; set; }
    public Model Model { get; set; } = null!;
    public int Number { get; set; }
    public string Sha256 { get; set; } = "";
    public string ArtifactKey { get; set; } = "";
    public long SizeBytes { get; set; }
    public ModelFormat Format { get; set; }
    public int? InputSize { get; set; }
    public string ClassesJson { get; set; } = "[]";
    public string MetadataJson { get; set; } = "{}";
    public string MetricsJson { get; set; } = "{}";
    public VersionSource Source { get; set; }
    public Guid? TrainingJobId { get; set; }
    public string? License { get; set; }
    public ModelStage Stage { get; set; }
    public string? StageChangedBy { get; set; }
    public DateTime? StageChangedAt { get; set; }
    public string? Notes { get; set; }
    public string CreatedBy { get; set; } = "";
    public DateTime CreatedAt { get; set; }
    /// <summary>선택적 백그라운드 세션 검증 결과: null(미실행) | Loaded | Failed</summary>
    public string? ValidationStatus { get; set; }
    public string WarningsJson { get; set; } = "[]";
    public List<ModelStageHistory> StageHistory { get; set; } = new();
}

public class ModelStageHistory
{
    public Guid Id { get; set; }
    public Guid ModelVersionId { get; set; }
    public ModelVersion ModelVersion { get; set; } = null!;
    public ModelStage FromStage { get; set; }
    public ModelStage ToStage { get; set; }
    public string ChangedBy { get; set; } = "";
    public DateTime ChangedAt { get; set; }
    public string? Reason { get; set; }
}

/// <summary>레시피 도구 → 모델 버전 바인딩 이력 (Phase 1 §3). RecipeId 는 BODA.VMS.Web 레시피의 외부 키(문자열).</summary>
public class ModelBinding
{
    public Guid Id { get; set; }
    public string RecipeId { get; set; } = "";
    public string ToolId { get; set; } = "";
    public Guid ModelId { get; set; }
    public Guid? ModelVersionId { get; set; }
    public BindingMode Mode { get; set; }
    public string BoundBy { get; set; } = "";
    public DateTime BoundAt { get; set; }
    public Guid? PreviousBindingId { get; set; }
    public bool IsActive { get; set; }
    public string? Reason { get; set; }
}

/// <summary>낙관적 동시성 토큰을 갖는 엔티티. SaveChanges 때 <see cref="MlopsDbContext"/> 가 새 값을 넣는다.</summary>
public interface IConcurrencyStamped
{
    /// <summary>SQLite 에는 rowversion 이 없어 Guid 를 직접 갱신한다</summary>
    Guid Stamp { get; set; }
}

/// <summary>학습 워커 (Phase 3 §3). 토큰은 SHA-256 해시로만 저장.</summary>
public class Worker : IConcurrencyStamped
{
    public Guid Id { get; set; }
    public Guid Stamp { get; set; }
    public string Name { get; set; } = "";
    public string? MachineName { get; set; }
    public string TokenHash { get; set; } = "";
    public WorkerStatus Status { get; set; } = WorkerStatus.Offline;
    public bool AdminDisabled { get; set; }
    public string? DisabledReason { get; set; }
    public DateTime? LastHeartbeatAt { get; set; }
    public string? CapabilitiesJson { get; set; }
    public string TaskTypesJson { get; set; } = "[]";
    public int MaxConcurrent { get; set; } = 1;
    public Guid? CurrentJobId { get; set; }
    public DateTime CreatedAt { get; set; }
    public string? WorkerVersion { get; set; }
    public double DiskFreeGB { get; set; }
    public int GpuMemFreeMB { get; set; }
    public int ProtocolVersion { get; set; }
}

/// <summary>
/// 학습 작업 (Phase 3 §3). 상태는 서버 소유.
/// 감독자(재큐)와 워커 보고(ack/progress/finish)가 같은 행을 동시에 고칠 수 있어 <see cref="Stamp"/> 로 보호한다.
/// 없으면 감독자가 읽은 뒤 워커가 ack 한 사이에 감독자의 저장이 ack 을 덮어써, 워커는 학습 중인데 서버는 미배정으로 보고
/// 같은 작업이 두 번째 워커에 배정된다.
/// </summary>
public class TrainingJob : IConcurrencyStamped
{
    public Guid Id { get; set; }
    public Guid Stamp { get; set; }
    public string? ProjectId { get; set; }
    public Guid DatasetVersionId { get; set; }
    public Guid ModelId { get; set; }
    public TaskType TaskType { get; set; }
    public TrainingScript Script { get; set; }
    public string? Backbone { get; set; }
    public string? PretrainedRef { get; set; }
    public string HyperparamsJson { get; set; } = "{}";
    public int Seed { get; set; }
    public TrainingJobState State { get; set; }
    public int Priority { get; set; }
    public Guid? WorkerId { get; set; }
    public DateTime? AssignedAt { get; set; }
    public DateTime? AckedAt { get; set; }
    public DateTime? StartedAt { get; set; }
    public DateTime? FinishedAt { get; set; }
    public int Attempt { get; set; }
    public int MaxAttempts { get; set; } = 2;
    public double Progress { get; set; }
    public int CurrentEpoch { get; set; }
    public int TotalEpochs { get; set; }
    public double? LastLoss { get; set; }
    public double? LastMetric { get; set; }
    public string? Error { get; set; }
    public string? FailureKind { get; set; }
    public Guid? ResultModelVersionId { get; set; }
    public string? ReproducibilityJson { get; set; }
    public string CreatedBy { get; set; } = "";
    public DateTime CreatedAt { get; set; }
    public bool CancelRequested { get; set; }
    public string? CancelReason { get; set; }
    /// <summary>배정 시점의 스크립트 해시 — 워커는 이 해시와 일치하는 스크립트만 실행</summary>
    public string? ScriptSha256 { get; set; }
    /// <summary>동일 작업 경고용 키 (데이터셋·모델·스크립트·하이퍼파라미터·seed·사전학습·백본)</summary>
    public string DedupKey { get; set; } = "";
    public DateTime? LastProgressAt { get; set; }
    public long LogSeq { get; set; }
    /// <summary>YOLO(AGPL) 작업의 라이선스 확인 문자열 → 결과 버전 License</summary>
    public string? License { get; set; }
}

public class JobLogChunk
{
    public long Id { get; set; }
    public Guid JobId { get; set; }
    public long Seq { get; set; }
    public JobLogLevel Level { get; set; }
    public string Text { get; set; } = "";
    public DateTime At { get; set; }
}

public class JobArtifact
{
    public Guid Id { get; set; }
    public Guid JobId { get; set; }
    public JobArtifactKind Kind { get; set; }
    public string FileName { get; set; } = "";
    public string StorageKey { get; set; } = "";
    public string Sha256 { get; set; } = "";
    public long SizeBytes { get; set; }
    public DateTime CreatedAt { get; set; }
}

/// <summary>사전학습 가중치 미러 자산 (Phase 3 §3). Files 는 [{fileName, sha256, sizeBytes}] JSON.</summary>
public class PretrainedAsset
{
    public string Ref { get; set; } = "";
    public string FilesJson { get; set; } = "[]";
    public string? License { get; set; }
    public string? Source { get; set; }
    public string AddedBy { get; set; } = "";
    public DateTime AddedAt { get; set; }
}

/// <summary>
/// 학습이 대상으로 삼는 불변 스냅샷 (개발 문서 §5.2 — 학습은 항상 버전에 대해 실행).
/// 만들어지는 길이 둘이다: 플랫폼 데이터셋을 굳힌 Snapshot, 밖에서 만든 zip 을 올린 Upload.
/// Snapshot 은 매니페스트만 먼저 굳히고, 내보내기 zip 은 처음 요청될 때 만들어 캐시한다.
/// </summary>
public class DatasetVersion
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    public TaskType TaskType { get; set; }
    public string ExportFormat { get; set; } = "yolo";
    /// <summary>
    /// 이미지 목록·라벨·분할을 합쳐 만든 해시 — <b>내용의 신원</b>이다. 재현성 레코드가 이 값을 남긴다.
    /// 같은 이미지·라벨·분할이면 언제 떠도 같은 값이고, 그래서 zip 바이트와는 무관하다.
    /// 받은 파일을 검증하는 데 쓰지 마세요 — 그 용도는 <see cref="ExportSha256"/> 입니다.
    /// </summary>
    public string ManifestHash { get; set; } = "";
    /// <summary>
    /// 내보내기 zip <b>파일</b>의 SHA-256. 받는 쪽이 끝까지 제대로 받았는지 대조하는 값이다.
    /// zip 을 굽기 전에는 없다 (Snapshot 은 처음 내보낼 때 채워진다).
    /// </summary>
    public string? ExportSha256 { get; set; }
    /// <summary>내보내기 zip 의 위치. Snapshot 은 처음 내보낼 때 채워진다.</summary>
    public string? StorageKey { get; set; }
    public long SizeBytes { get; set; }
    public int ImageCount { get; set; }
    public int AnnotationCount { get; set; }
    public string ClassesJson { get; set; } = "[]";
    public string CreatedBy { get; set; } = "";
    public DateTime CreatedAt { get; set; }

    public DatasetVersionSource Source { get; set; }
    /// <summary>Snapshot 이면 원본 데이터셋</summary>
    public Guid? DatasetId { get; set; }
    /// <summary>Snapshot 의 매니페스트(JSON) 위치 — 이미지 id·라벨·분할이 굳어 있다</summary>
    public string? ManifestKey { get; set; }
    /// <summary>분할별 장 수 요약 (json)</summary>
    public string SplitCountsJson { get; set; } = "{}";
}

/// <summary>감사 로그 — 카테고리 Model / Dataset / Training / Worker (개발 문서 §7)</summary>
public class AuditLog
{
    public long Id { get; set; }
    public string Category { get; set; } = "";
    public string Action { get; set; } = "";
    public string Actor { get; set; } = "";
    public DateTime At { get; set; }
    public string? EntityId { get; set; }
    public string? DetailsJson { get; set; }
}
