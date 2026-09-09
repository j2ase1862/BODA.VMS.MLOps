using BODA.VMS.MLOps.Core.Labeling;

namespace BODA.VMS.MLOps.Core.Inference;

/// <summary>검출 하나. 좌표는 원본 기준 0~1 정규화.</summary>
public sealed record Detection(NormBox Box, int ClassIndex, double Score);

/// <summary>
/// 검출 모델 출력 → 라벨 후보 (개발 문서 §5.4 Active Learning).
///
/// <para>
/// 규약 두 가지를 다룬다. VMS 런타임이 이미 이 둘을 판별해 쓰고 있고, 레지스트리도 업로드할 때
/// 같은 판별을 한다 (<c>ModelFormat</c>). 그 판별 결과를 그대로 받아 후처리만 한다.
/// </para>
/// <list type="bullet">
/// <item><b>YOLO</b> — <c>output0[1, 4+nc, N]</c>. 앞 4행이 cx·cy·w·h (입력 좌표계),
/// 나머지가 클래스별 점수다. 겹치는 상자가 그대로 나오므로 NMS 가 필요하다.</item>
/// <item><b>D-FINE deploy</b> — <c>labels[1,N] · boxes[1,N,4] · scores[1,N]</c>.
/// 상자는 이미 원본 픽셀 좌표이고 NMS 도 끝나 있다.</item>
/// </list>
/// <para>
/// 여기는 순수 계산만 둔다. 세션을 여는 일과 픽셀을 옮기는 일은 서버가 한다.
/// </para>
/// </summary>
public static class DetectionPostProcess
{
    /// <summary>이보다 겹치면 같은 물체로 보고 점수가 낮은 쪽을 버린다.</summary>
    public const double DefaultIouThreshold = 0.45;

    /// <summary>한 장에서 남길 상자 수 상한. 사람이 손볼 수 있는 수준으로 자른다.</summary>
    public const int MaxDetections = 100;

    /// <summary>
    /// YOLO 검출 출력 후처리.
    /// </summary>
    /// <param name="output">output0 의 값 (행 우선, [channels × anchors])</param>
    /// <param name="channels">4 + 클래스 수</param>
    /// <param name="anchors">후보 수 (예: 8400)</param>
    /// <param name="box">입력 전처리에 쓴 레터박스</param>
    public static List<Detection> Yolo(
        ReadOnlySpan<float> output, int channels, int anchors, LetterBox box,
        double confidence = 0.25, double iou = DefaultIouThreshold)
    {
        var found = new List<Detection>();
        if (channels <= 4 || anchors <= 0 || output.Length < (long)channels * anchors) return found;

        int classCount = channels - 4;
        for (int a = 0; a < anchors; a++)
        {
            // 클래스별 점수 중 가장 높은 것만 본다. YOLOv8 부터는 objectness 가 따로 없다.
            int best = -1;
            float bestScore = 0;
            for (int c = 0; c < classCount; c++)
            {
                float score = output[(4 + c) * anchors + a];
                if (score > bestScore) { bestScore = score; best = c; }
            }
            if (best < 0 || bestScore < confidence) continue;

            float cx = output[0 * anchors + a];
            float cy = output[1 * anchors + a];
            float w = output[2 * anchors + a];
            float h = output[3 * anchors + a];
            if (w <= 0 || h <= 0) continue;

            found.Add(new Detection(
                box.BoxToNormalized(cx - w / 2, cy - h / 2, cx + w / 2, cy + h / 2), best, bestScore));
        }

        return NonMaxSuppression(found, iou);
    }

    /// <summary>
    /// D-FINE deploy 출력 후처리. 상자가 이미 원본 픽셀이라 정규화만 한다.
    /// 모델이 고정 개수(예: 300)를 내놓고 뒤쪽은 점수가 0 이므로 문턱으로 자른다.
    /// </summary>
    public static List<Detection> DFine(
        ReadOnlySpan<long> labels, ReadOnlySpan<float> boxes, ReadOnlySpan<float> scores,
        int count, int originalWidth, int originalHeight, double confidence = 0.25)
    {
        var found = new List<Detection>();
        if (count <= 0 || originalWidth <= 0 || originalHeight <= 0) return found;
        if (labels.Length < count || scores.Length < count || boxes.Length < count * 4) return found;

        for (int i = 0; i < count && found.Count < MaxDetections; i++)
        {
            double score = scores[i];
            if (score < confidence) continue;

            double x1 = boxes[i * 4 + 0] / originalWidth;
            double y1 = boxes[i * 4 + 1] / originalHeight;
            double x2 = boxes[i * 4 + 2] / originalWidth;
            double y2 = boxes[i * 4 + 3] / originalHeight;

            var normalized = new NormBox(
                Math.Clamp(Math.Min(x1, x2), 0, 1),
                Math.Clamp(Math.Min(y1, y2), 0, 1),
                Math.Abs(x2 - x1), Math.Abs(y2 - y1)).Clamped();
            if (normalized.Width <= 0 || normalized.Height <= 0) continue;

            found.Add(new Detection(normalized, (int)labels[i], score));
        }
        return [.. found.OrderByDescending(d => d.Score)];
    }

    /// <summary>겹치는 상자 중 점수가 높은 것만 남긴다. 클래스별로 따로 본다.</summary>
    public static List<Detection> NonMaxSuppression(List<Detection> detections, double iouThreshold)
    {
        var kept = new List<Detection>();
        foreach (var candidate in detections.OrderByDescending(d => d.Score))
        {
            if (kept.Count >= MaxDetections) break;
            bool overlaps = kept.Any(k => k.ClassIndex == candidate.ClassIndex
                                          && Iou(k.Box, candidate.Box) > iouThreshold);
            if (!overlaps) kept.Add(candidate);
        }
        return kept;
    }

    public static double Iou(NormBox a, NormBox b)
    {
        double left = Math.Max(a.X, b.X), top = Math.Max(a.Y, b.Y);
        double right = Math.Min(a.X + a.Width, b.X + b.Width);
        double bottom = Math.Min(a.Y + a.Height, b.Y + b.Height);
        double overlap = Math.Max(0, right - left) * Math.Max(0, bottom - top);
        double union = a.Area + b.Area - overlap;
        return union <= 0 ? 0 : overlap / union;
    }
}

/// <summary>
/// 이 이미지를 사람이 먼저 봐야 하는 정도 (개발 문서 §5.4 Active Learning).
///
/// <para>
/// 규칙은 둘이다. 아무것도 못 찾았으면 가장 애매하다 — 모델이 모르는 그림이라는 뜻이라
/// 사람이 보는 값이 가장 크다. 찾았으면 그중 <b>가장 확신 없는 상자</b>가 그 이미지를 대표한다.
/// 0.99 짜리 하나와 0.3 짜리 하나가 함께 있으면 그 0.3 이 문제이기 때문이다.
/// </para>
/// <para>
/// 값은 0~1 이고 클수록 애매하다. 척도는 모델마다 다르므로 서버는 이 값을 해석하지 않고
/// 라벨링 큐의 순서에만 쓴다.
/// </para>
/// </summary>
public static class UncertaintyScore
{
    public static double ForDetections(IReadOnlyList<Detection> detections) =>
        detections.Count == 0 ? 1.0 : Math.Clamp(1.0 - detections.Min(d => d.Score), 0, 1);
}
