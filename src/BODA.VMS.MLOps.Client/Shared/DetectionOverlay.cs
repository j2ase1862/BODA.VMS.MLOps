using BODA.VMS.MLOps.Contracts.Models;

namespace BODA.VMS.MLOps.Client.Shared;

/// <summary>
/// 검출 결과를 사진 위에 겹쳐 그리는 규칙 — 모델 돌려 보기와 새 사진 훑기가 같이 쓴다.
/// 두 화면이 같은 문턱에서 다른 상자를 보이면 사람이 무엇을 믿을지 모른다.
/// </summary>
public static class DetectionOverlay
{
    /// <summary>신뢰도 문턱을 넘은 것에 클래스별 NMS — 라인 런타임(VMS DetectionTool.GlobalNMS)과 같은 규칙</summary>
    public static List<TryDetectionDto> Visible(IEnumerable<TryDetectionDto> detections, double confidence, double iou)
    {
        var kept = new List<TryDetectionDto>();
        foreach (var d in detections.Where(d => d.Score >= confidence).OrderByDescending(d => d.Score))
            if (!kept.Any(k => k.ClassIndex == d.ClassIndex && Iou(k, d) > iou))
                kept.Add(d);
        return kept;
    }

    public static double Iou(TryDetectionDto a, TryDetectionDto b)
    {
        double x1 = Math.Max(a.X, b.X), y1 = Math.Max(a.Y, b.Y);
        double x2 = Math.Min(a.X + a.W, b.X + b.W), y2 = Math.Min(a.Y + a.H, b.Y + b.H);
        double inter = Math.Max(0, x2 - x1) * Math.Max(0, y2 - y1);
        double union = a.W * a.H + b.W * b.H - inter;
        return union <= 0 ? 0 : inter / union;
    }

    /// <summary>
    /// 한 장의 판정 문구. 후보가 없을 때 가장 높았던 점수를 함께 말한다 —
    /// 문턱이 높은 것인지, 모델이 정말 못 보는 것인지 가른다 (WPF 미리보기의 진단과 같다).
    /// </summary>
    public static string Verdict(string? error, double? maxScore, IReadOnlyList<TryDetectionDto> visible, double scoreFloor)
    {
        if (error is not null) return $"돌리지 못함 — {error}";
        if (visible.Count > 0)
            return $"후보 {visible.Count}개 · 최고 {visible[0].Score:0.00} · " +
                   string.Join(", ", visible.GroupBy(b => b.ClassName).Select(g => $"{g.Key} {g.Count()}"));
        return maxScore is { } max
            ? $"후보 없음 (최고 {max:0.00} — 신뢰도를 낮추면 보입니다)"
            : $"후보 없음 (모든 후보가 {scoreFloor:0.00} 미만)";
    }

    public static string BoxStyle(TryDetectionDto d) =>
        FormattableString.Invariant(
            $"position:absolute; left:{d.X * 100:0.###}%; top:{d.Y * 100:0.###}%; width:{d.W * 100:0.###}%; height:{d.H * 100:0.###}%; border:2px solid {ClassPalette.ColorOf(d.ClassIndex)}; box-sizing:border-box; pointer-events:none");

    public static string TagStyle(TryDetectionDto d) =>
        $"position:absolute; left:-2px; top:-20px; line-height:18px; padding:0 4px; font-size:12px; white-space:nowrap; color:#fff; background:{ClassPalette.ColorOf(d.ClassIndex)}";
}
