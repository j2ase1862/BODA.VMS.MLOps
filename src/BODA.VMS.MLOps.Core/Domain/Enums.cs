namespace BODA.VMS.MLOps.Core.Domain;

/// <summary>검사 모델 작업 유형 — Dataset·Model 생성 후 불변 (개발 문서 §4)</summary>
public enum TaskType { Detection, Classification, Anomaly, Segmentation, Ocr }

/// <summary>ONNX 규약 (개발 문서 부록 B). Unknown 은 등록 허용하되 경고.</summary>
public enum ModelFormat { Unknown, DFine, Yolo, YoloSeg, Classifier, Anomaly, PpOcr }

/// <summary>Candidate(등록 직후) → Staging(테스트 라인) → Production(운영) → Retired</summary>
public enum ModelStage { Candidate, Staging, Production, Retired }

/// <summary>ModelVersion 출처</summary>
public enum VersionSource { Upload, TrainingJob }

/// <summary>레시피 도구 바인딩 모드 — 특정 버전 고정 또는 Production 추종</summary>
public enum BindingMode { Pinned, FollowProduction }

/// <summary>학습 작업 상태 (Phase 3 §3). 서버가 소유하고 워커 보고로만 진행한다.</summary>
public enum TrainingJobState
{
    Queued, Assigned, Preparing, Running, Exporting, Uploading, Succeeded, Failed, Cancelled
}

/// <summary>학습 스크립트 — 서버 scripts\ 폴더의 파일명과 1:1</summary>
public enum TrainingScript { TrainDfine, TrainYolo, TrainClassifier, TrainAnomaly, TrainPpocr, TrainRfdetrSeg }

public enum WorkerStatus { Online, Busy, Offline, Disabled }

public enum JobLogLevel { Info, Warn, Error, Stdout, Stderr }

public enum JobArtifactKind { Onnx, Metrics, Curve, TrainInfo, Log }

public static class TrainingScriptExtensions
{
    public static string FileName(this TrainingScript s) => s switch
    {
        TrainingScript.TrainDfine => "train_dfine.py",
        TrainingScript.TrainYolo => "train_yolo.py",
        TrainingScript.TrainClassifier => "train_classifier.py",
        TrainingScript.TrainAnomaly => "train_anomaly.py",
        TrainingScript.TrainPpocr => "train_ppocr.py",
        TrainingScript.TrainRfdetrSeg => "train_rfdetr_seg.py",
        _ => throw new ArgumentOutOfRangeException(nameof(s))
    };

    /// <summary>스크립트가 산출하는 작업 유형</summary>
    public static TaskType TaskType(this TrainingScript s) => s switch
    {
        TrainingScript.TrainDfine or TrainingScript.TrainYolo => Domain.TaskType.Detection,
        TrainingScript.TrainClassifier => Domain.TaskType.Classification,
        TrainingScript.TrainAnomaly => Domain.TaskType.Anomaly,
        TrainingScript.TrainPpocr => Domain.TaskType.Ocr,
        TrainingScript.TrainRfdetrSeg => Domain.TaskType.Segmentation,
        _ => throw new ArgumentOutOfRangeException(nameof(s))
    };

    /// <summary>워커·WPF 가 같은 데이터셋 내보내기 형식을 읽는다 (개발 문서 §5.2)</summary>
    public static string DatasetExportFormat(this TrainingScript s) => s switch
    {
        TrainingScript.TrainDfine or TrainingScript.TrainYolo => "yolo",
        TrainingScript.TrainClassifier => "imagefolder",
        TrainingScript.TrainAnomaly => "mvtec",
        TrainingScript.TrainPpocr => "ppocr",
        TrainingScript.TrainRfdetrSeg => "coco",
        _ => throw new ArgumentOutOfRangeException(nameof(s))
    };

    /// <summary>스크립트가 산출하는 ONNX 규약 (아티팩트 등록 시 판별 결과와 대조)</summary>
    public static ModelFormat ExpectedFormat(this TrainingScript s) => s switch
    {
        TrainingScript.TrainDfine => ModelFormat.DFine,
        TrainingScript.TrainYolo => ModelFormat.Yolo,
        TrainingScript.TrainClassifier => ModelFormat.Classifier,
        TrainingScript.TrainAnomaly => ModelFormat.Anomaly,
        TrainingScript.TrainPpocr => ModelFormat.PpOcr,
        TrainingScript.TrainRfdetrSeg => ModelFormat.Unknown,
        _ => ModelFormat.Unknown
    };
}
