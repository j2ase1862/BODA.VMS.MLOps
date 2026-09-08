using BODA.VMS.MLOps.Core.Domain;

namespace BODA.VMS.MLOps.Contracts.Datasets;

/// <summary>
/// Phase 2(데이터 관리) 의 최소 선행 구현 — 학습 워커가 소비하는 "데이터셋 버전 = 내보내기 zip + manifest 해시".
/// 이미지 풀·라벨링·스냅샷 생성은 Phase 2 에서 이 엔티티 위에 얹는다.
/// </summary>
public sealed record DatasetVersionDto(
    Guid Id, string Name, TaskType TaskType, string ExportFormat, string ManifestHash, long SizeBytes,
    int ImageCount, string[] Classes, string CreatedBy, DateTime CreatedAt, string ExportUrl,
    DatasetVersionSource Source = DatasetVersionSource.Upload,
    /// <summary>스냅샷이면 원본 데이터셋</summary>
    Guid? DatasetId = null,
    int AnnotationCount = 0,
    Dictionary<string, int>? SplitCounts = null,
    /// <summary>내보내기 zip 이 이미 만들어져 있는지. 스냅샷은 처음 요청될 때 만들어진다.</summary>
    bool ExportReady = true);

/// <summary>multipart 'meta' 파트 — zip 과 함께 업로드</summary>
public sealed record DatasetVersionUploadMeta(string Name, TaskType TaskType, string ExportFormat, int ImageCount = 0, string[]? Classes = null);
