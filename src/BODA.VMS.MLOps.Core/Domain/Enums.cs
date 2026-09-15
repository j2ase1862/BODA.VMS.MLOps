namespace BODA.VMS.MLOps.Core.Domain;

/// <summary>검사 모델 작업 유형 — Dataset·Model 생성 후 불변 (개발 문서 §4)</summary>
public enum TaskType { Detection, Classification, Anomaly, Segmentation, Ocr }

/// <summary>ONNX 규약 (개발 문서 부록 B). Unknown 은 등록 허용하되 경고.</summary>
public enum ModelFormat { Unknown, DFine, Yolo, YoloSeg, Classifier, Anomaly, PpOcr, RfdetrSeg }

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

/// <summary>
/// 스크립트가 <c>--pretrained</c> 를 어떻게 다루는지. 하나로 묶을 수 없어 스크립트별로 갖는다.
///
/// <para>워커는 학습을 <b>항상 오프라인</b>으로 돌린다(<c>HF_HUB_OFFLINE=1</c>·<c>TRANSFORMERS_OFFLINE=1</c>).
/// 그래서 가중치를 받아 와야 하는 스크립트는 사전학습 미러가 없으면 반드시 실패하는데, 그 사실이
/// <b>작업이 워커에 배정되어 실제로 돌기 시작한 뒤에야</b> 드러났다. 제출 시점에 막는다.</para>
/// </summary>
public enum PretrainedRequirement
{
    /// <summary>미러가 없으면 돌 수 없다 — 제출 시점에 막는다.</summary>
    Required,
    /// <summary>있으면 쓰고 없으면 기본값으로 돈다.</summary>
    Optional,
    /// <summary>스크립트가 <c>--pretrained</c> 를 받지 못하거나 경로를 받지 못한다 — 주면 실행이 깨진다.</summary>
    NotSupported,
}

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

    /// <summary>
    /// 사전학습 미러(<c>PretrainedRef</c>) 요구 — 스크립트의 실제 <c>--pretrained</c> 계약에서 나온다.
    ///
    /// <list type="bullet">
    /// <item><b>TrainDfine</b> — <c>DFineForObjectDetection.from_pretrained(&lt;HF id 또는 로컬 폴더&gt;)</c>.
    /// 워커가 <c>TRANSFORMERS_OFFLINE=1</c> 로 돌리므로 로컬 폴더가 없으면 <b>반드시</b> 실패한다 → Required.</item>
    /// <item><b>TrainRfdetrSeg</b> — <c>preview/small/…</c> 는 인터넷에서 받아 오고, 경로를 주면 그 체크포인트를 쓴다.
    /// 폐쇄망 워커에서는 경로가 있어야 한다 → Required.</item>
    /// <item><b>TrainClassifier</b> — <c>--pretrained</c> 가 <b>경로가 아니라 아키텍처 이름</b>이다
    /// (<c>getattr(torchvision.models, name)</c>). 미러 폴더를 주면 "지원하지 않는 모델" 로 즉시 죽는다 → NotSupported.
    /// (가중치는 torchvision 이 자체 캐시에서 받는다 — 폐쇄망 지원은 스크립트를 고쳐야 하는 별건이다.)</item>
    /// <item><b>TrainAnomaly</b> — 스크립트에 <c>--pretrained</c> 인자가 <b>아예 없다</b>(<c>--backbone</c> 만 있다).
    /// 주면 argparse 가 "unrecognized arguments" 로 끝낸다 → NotSupported.</item>
    /// <item><b>TrainYolo</b> — ultralytics 가 <c>yolov8n.pt</c> 를 GitHub 에서 받는다(HF 오프라인 설정과 무관).
    /// 인터넷이 있는 워커면 미러 없이도 돈다 → Optional.</item>
    /// <item><b>TrainPpocr</b> — 스크립트가 "(선택)" 이라고 못박고 있다 → Optional.</item>
    /// </list>
    /// </summary>
    public static PretrainedRequirement PretrainedRequirement(this TrainingScript s) => s switch
    {
        TrainingScript.TrainDfine or TrainingScript.TrainRfdetrSeg => Domain.PretrainedRequirement.Required,
        TrainingScript.TrainClassifier or TrainingScript.TrainAnomaly => Domain.PretrainedRequirement.NotSupported,
        _ => Domain.PretrainedRequirement.Optional,
    };

    /// <summary>스크립트가 산출하는 ONNX 규약 (아티팩트 등록 시 판별 결과와 대조)</summary>
    public static ModelFormat ExpectedFormat(this TrainingScript s) => s switch
    {
        TrainingScript.TrainDfine => ModelFormat.DFine,
        TrainingScript.TrainYolo => ModelFormat.Yolo,
        TrainingScript.TrainClassifier => ModelFormat.Classifier,
        TrainingScript.TrainAnomaly => ModelFormat.Anomaly,
        TrainingScript.TrainPpocr => ModelFormat.PpOcr,
        TrainingScript.TrainRfdetrSeg => ModelFormat.RfdetrSeg,
        _ => ModelFormat.Unknown
    };
}
