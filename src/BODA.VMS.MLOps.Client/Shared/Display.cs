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

    public static string SplitText(DatasetSplit split) => split switch
    {
        DatasetSplit.Train => "학습",
        DatasetSplit.Val => "검증",
        _ => "시험",
    };

    public static string ImageSourceText(ImageSource source) => source switch
    {
        ImageSource.Manual => "직접 업로드",
        ImageSource.LineNg => "라인 NG",
        ImageSource.ActiveLearning => "능동 학습",
        _ => source.ToString(),
    };
}
