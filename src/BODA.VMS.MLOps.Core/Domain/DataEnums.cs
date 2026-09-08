namespace BODA.VMS.MLOps.Core.Domain;

/// <summary>이미지가 어디서 왔는지 (개발 문서 §4 ImageSource)</summary>
public enum ImageSource
{
    /// <summary>사람이 직접 올림</summary>
    Manual,
    /// <summary>라인 PC 의 NG 이미지 업로드 — inspectionId 로 생산 이력과 이어진다</summary>
    LineNg,
    /// <summary>Active Learning 이 불확실도 순으로 골라 담음</summary>
    ActiveLearning,
}

/// <summary>라벨 도형. 작업 유형이 어떤 도형을 쓰는지는 <see cref="AnnotationShapeExtensions.AllowedFor"/> 참조.</summary>
public enum AnnotationShape
{
    /// <summary>축 정렬 사각형 (검출·OCR)</summary>
    Box,
    /// <summary>다각형 (세그멘테이션). 검출 학습에는 외접 박스로 변환된다.</summary>
    Polygon,
    /// <summary>이미지 전체에 붙는 클래스 (분류·이상탐지)</summary>
    Classification,
    /// <summary>사각형 + 텍스트 (OCR)</summary>
    Text,
}

/// <summary>데이터셋 버전 스냅샷에서의 분할</summary>
public enum DatasetSplit { Train, Val, Test }

/// <summary>이미지 한 장의 라벨 진행 상태 (데이터셋 안에서)</summary>
public enum LabelStatus
{
    Unlabeled,
    InProgress,
    Labeled,
    Reviewed,
}

/// <summary>데이터셋 버전이 어떻게 만들어졌는지</summary>
public enum DatasetVersionSource
{
    /// <summary>플랫폼 안의 데이터셋을 그 시점 그대로 굳힌 것</summary>
    Snapshot,
    /// <summary>밖에서 만든 내보내기 zip 을 올린 것 (WPF 도구 등)</summary>
    Upload,
}

public static class AnnotationShapeExtensions
{
    /// <summary>작업 유형별로 허용되는 도형. 화면과 서버가 같은 규칙을 쓴다.</summary>
    public static AnnotationShape[] AllowedFor(this TaskType taskType) => taskType switch
    {
        TaskType.Detection => [AnnotationShape.Box, AnnotationShape.Polygon],
        TaskType.Segmentation => [AnnotationShape.Polygon],
        TaskType.Classification => [AnnotationShape.Classification],
        TaskType.Anomaly => [AnnotationShape.Classification],
        TaskType.Ocr => [AnnotationShape.Text],
        _ => [],
    };

    /// <summary>이미지 한 장에 하나만 붙는 도형인가 (분류·이상탐지)</summary>
    public static bool IsSingleValued(this AnnotationShape shape) => shape == AnnotationShape.Classification;
}
