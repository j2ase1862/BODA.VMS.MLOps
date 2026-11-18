using BODA.VMS.MLOps.Contracts.Training;
using BODA.VMS.MLOps.Core.Domain;
using MudBlazor;

namespace BODA.VMS.MLOps.Client.Shared;

/// <summary>목록·상세 화면이 공유하는 표시 규칙. 색과 문구를 한 곳에 모아 화면마다 달라지지 않게 한다.</summary>
public static class Display
{
    /// <summary>스테이지 배지 색 — Production 이 한눈에 들어와야 한다</summary>
    public static Color StageColor(ModelStage stage) => stage switch
    {
        ModelStage.Production => Color.Success,
        ModelStage.Staging => Color.Info,
        ModelStage.Candidate => Color.Default,
        ModelStage.Retired => Color.Dark,
        _ => Color.Default,
    };

    public static Color JobColor(TrainingJobState state) => state switch
    {
        TrainingJobState.Succeeded => Color.Success,
        TrainingJobState.Failed => Color.Error,
        TrainingJobState.Cancelled => Color.Dark,
        TrainingJobState.Queued => Color.Default,
        _ => Color.Info,
    };

    public static Color WorkerColor(WorkerStatus status) => status switch
    {
        WorkerStatus.Online => Color.Success,
        WorkerStatus.Busy => Color.Info,
        WorkerStatus.Offline => Color.Default,
        WorkerStatus.Disabled => Color.Error,
        _ => Color.Default,
    };

    public static string StageText(ModelStage stage) => stage switch
    {
        ModelStage.Candidate => "후보",
        ModelStage.Staging => "스테이징",
        ModelStage.Production => "운영",
        ModelStage.Retired => "폐기",
        _ => stage.ToString(),
    };

    public static string JobStateText(TrainingJobState state) => state switch
    {
        TrainingJobState.Queued => "대기",
        TrainingJobState.Assigned => "배정됨",
        TrainingJobState.Preparing => "준비 중",
        TrainingJobState.Running => "학습 중",
        TrainingJobState.Exporting => "내보내는 중",
        TrainingJobState.Uploading => "업로드 중",
        TrainingJobState.Succeeded => "성공",
        TrainingJobState.Failed => "실패",
        TrainingJobState.Cancelled => "취소됨",
        _ => state.ToString(),
    };

    public static string TaskTypeText(TaskType t) => t switch
    {
        TaskType.Detection => "검출",
        TaskType.Classification => "분류",
        TaskType.Anomaly => "이상탐지",
        TaskType.Segmentation => "세그멘테이션",
        TaskType.Ocr => "OCR",
        _ => t.ToString(),
    };

    public static string WorkerStatusText(WorkerStatus s) => s switch
    {
        WorkerStatus.Online => "대기 중",
        WorkerStatus.Busy => "학습 중",
        WorkerStatus.Offline => "연결 끊김",
        WorkerStatus.Disabled => "비활성",
        _ => s.ToString(),
    };

    public static string FormatText(ModelFormat f) => f == ModelFormat.Unknown ? "판별 실패" : f.ToString().ToLowerInvariant();

    public static string Sha(string? sha) => string.IsNullOrEmpty(sha) ? "—" : sha[..Math.Min(12, sha.Length)];

    public static string Size(long bytes) => bytes switch
    {
        >= 1L << 30 => $"{bytes / (double)(1L << 30):0.##} GB",
        >= 1L << 20 => $"{bytes / (double)(1L << 20):0.##} MB",
        >= 1L << 10 => $"{bytes / (double)(1L << 10):0.##} KB",
        _ => $"{bytes} B",
    };

    /// <summary>
    /// 데이터셋 버전의 크기. 스냅샷은 zip 을 처음 받거나 학습에 쓸 때 만들므로 그 전에는 크기가 없다 —
    /// "0 B" 로 보이면 빈 판처럼 읽힌다 (ML-30).
    /// </summary>
    public static string VersionSize(long bytes, bool exportReady) => exportReady ? Size(bytes) : "받을 때 만듦";

    /// <summary>UTC 로 저장된 시각을 로컬로 보여 준다</summary>
    public static string When(DateTime? utc) =>
        utc is null ? "—" : DateTime.SpecifyKind(utc.Value, DateTimeKind.Utc).ToLocalTime().ToString("yyyy-MM-dd HH:mm");

    public static string Ago(DateTime? utc)
    {
        if (utc is null) return "—";
        var span = DateTime.UtcNow - DateTime.SpecifyKind(utc.Value, DateTimeKind.Utc);
        if (span.TotalSeconds < 60) return $"{Math.Max(0, (int)span.TotalSeconds)}초 전";
        if (span.TotalMinutes < 60) return $"{(int)span.TotalMinutes}분 전";
        if (span.TotalHours < 24) return $"{(int)span.TotalHours}시간 전";
        return $"{(int)span.TotalDays}일 전";
    }

    public static string Duration(DateTime? from, DateTime? to)
    {
        if (from is null) return "—";
        var span = (to ?? DateTime.UtcNow) - DateTime.SpecifyKind(from.Value, DateTimeKind.Utc);
        if (span.TotalMinutes < 1) return $"{Math.Max(0, (int)span.TotalSeconds)}초";
        if (span.TotalHours < 1) return $"{(int)span.TotalMinutes}분 {span.Seconds}초";
        return $"{(int)span.TotalHours}시간 {span.Minutes}분";
    }

    public static string ScriptText(TrainingScript s) => s.FileName();

    /// <summary>
    /// 학습이 멈춘 까닭. 서버가 주는 값은 기록·필터용 코드라 그대로 보이면 사람이 읽을 수 없다 (ML-19).
    /// 모르는 코드는 그대로 보여 준다 — 숨기면 새 실패 종류가 생겼을 때 아무 단서도 남지 않는다.
    /// </summary>
    public static string FailureKindText(string? kind) => kind switch
    {
        null or "" => "오류",
        FailureKinds.WorkerLost => "워커 연결이 끊김",
        FailureKinds.ScriptError => "학습 중 오류",
        FailureKinds.Stalled => "진행이 멈춤",
        FailureKinds.Timeout => "제한 시간 넘음",
        FailureKinds.PrepareError => "학습 준비 실패",
        FailureKinds.UploadError => "결과 올리기 실패",
        FailureKinds.AckTimeout => "워커가 작업을 받지 않음",
        FailureKinds.Oom => "GPU 메모리 부족",
        FailureKinds.YieldedToInspection => "검사에 GPU 를 내줌",
        _ => kind,
    };

    // ───────────── 데이터 관리·라벨링 ─────────────

    public static string LabelStatusText(LabelStatus s) => s switch
    {
        LabelStatus.Unlabeled => "미라벨",
        LabelStatus.InProgress => "작업 중",
        LabelStatus.Labeled => "라벨 완료",
        LabelStatus.Reviewed => "검토 완료",
        _ => s.ToString(),
    };

    public static Color LabelStatusColor(LabelStatus s) => s switch
    {
        LabelStatus.Reviewed => Color.Success,
        LabelStatus.Labeled => Color.Info,
        LabelStatus.InProgress => Color.Warning,
        _ => Color.Default,
    };

    public static string ShapeText(AnnotationShape shape) => shape switch
    {
        AnnotationShape.Box => "사각형",
        AnnotationShape.Polygon => "폴리곤",
        AnnotationShape.Classification => "분류",
        AnnotationShape.Text => "텍스트",
        _ => shape.ToString(),
    };

    /// <summary>
    /// 라벨링 캔버스 아래 안내 한 줄. 지금 고른 도구(<paramref name="mode"/>)와 데이터셋 작업 유형이 허용하는 도형으로 고른다.
    /// 폴리곤 고치는 법은 폴리곤을 쓰는 데이터셋(검출·세그멘테이션)에서만 붙인다 — 분류 화면에 그 문구가 떠 있으면
    /// 할 수 없는 일을 안내하게 된다.
    /// </summary>
    public static string LabelingHint(TaskType? taskType, string mode)
    {
        var parts = new List<string>
        {
            mode == "classification"
                ? "←/→ 이전·다음 이미지(저장하고 넘어감) · 숫자키나 클래스 버튼으로 이 사진의 클래스 붙이기 · 휠 확대 · Alt+끌기 이동"
                : "←/→ 이전·다음 이미지(저장하고 넘어감) · 끌어서 그리기 · 휠 확대 · Alt+끌기 이동 · 숫자키로 다음에 그릴 클래스 · Delete 삭제 · Esc 선택 해제",
        };
        if (mode == "polygon") parts.Add("클릭으로 점 추가, 오른쪽 클릭이나 Enter 로 닫기");
        if (mode == "sam")
            parts.Add("객체를 클릭하면 경계를 만들어 줍니다. Shift+클릭(또는 오른쪽 클릭)은 빼고 싶은 곳, Tab 으로 더 크게·작게, Backspace 는 마지막 점 취소");
        if (taskType is { } t && t.AllowedFor().Contains(AnnotationShape.Polygon))
            parts.Add("폴리곤은 꼭짓점을 끌어 고치고, 선을 누르면 꼭짓점이 생기며, Ctrl+클릭으로 뺍니다");
        return string.Join(" · ", parts);
    }

    public static string SplitText(DatasetSplit split) => split switch
    {
        DatasetSplit.Train => "학습",
        DatasetSplit.Val => "검증",
        _ => "시험",
    };

    /// <summary>
    /// 모니터링 신호(Phase 5). 문자열로 오는 이유는 판단 규칙이 Core 에 있고 화면은 그 이름만 받기 때문이다.
    /// </summary>
    public static string SignalText(string signal) => signal switch
    {
        "Steady" => "이상 없음",
        "NotEnoughData" => "판단 보류",
        "NgRateRose" => "불량률 상승",
        "ConfidenceDropped" => "신뢰도 하락",
        "InputDrifted" => "입력 변화",
        _ => signal,
    };

    /// <summary>
    /// "판단 보류" 를 초록으로 칠하지 않는다 — 괜찮다는 뜻이 아니라 아직 모른다는 뜻이다.
    /// </summary>
    public static Color SignalColor(string signal) => signal switch
    {
        "Steady" => Color.Success,
        "NotEnoughData" => Color.Default,
        _ => Color.Warning,
    };

    public static string ImageSourceText(ImageSource source) => source switch
    {
        ImageSource.Manual => "직접 업로드",
        ImageSource.LineNg => "라인 NG",
        ImageSource.ActiveLearning => "능동 학습",
        _ => source.ToString(),
    };
}
