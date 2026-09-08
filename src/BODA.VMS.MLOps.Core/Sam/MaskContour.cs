namespace BODA.VMS.MLOps.Core.Sam;

/// <summary>마스크에서 뽑아낸 도형. 좌표는 0~1 정규화.</summary>
public sealed record MaskShape(
    IReadOnlyList<Labeling.NormPoint> Polygon,
    Labeling.NormBox Box,
    /// <summary>마스크 전체에서 이 덩어리가 차지한 픽셀 수 — 클릭이 헛나갔는지 판단하는 데 쓴다.</summary>
    int PixelArea,
    /// <summary>
    /// 이 마스크가 나뉜 덩어리 수. 2 이상이면 여기 담긴 것은 그중 하나뿐이다.
    /// 가려진 물체를 집으면 이런 일이 생기고, 화면이 "조각이 더 있다" 고 알려 줄 수 있다.
    /// </summary>
    int PartCount = 1);

/// <summary>
/// 이진 마스크 → 폴리곤 (개발 문서 §5.4 SAM 보조).
///
/// <para>
/// WPF 쪽은 OpenCV 의 FindContours + ApproxPolyDP 를 썼지만, 이 서버는 이미지 처리를
/// SkiaSharp 로만 하고 OpenCV 네이티브를 들이지 않는다 (배포 크기·라이선스 허용 목록 §8).
/// 필요한 것은 "덩어리 하나의 바깥 윤곽" 뿐이라 여기서 직접 구현한다.
/// </para>
/// <para>
/// 순서: 연결 요소 라벨링(4-이웃) → 덩어리 선택(클릭한 자리 우선) → Moore 이웃 경계 추적(8-이웃)
/// → Douglas–Peucker 단순화 → 점 수 상한 맞추기.
/// </para>
/// </summary>
public static class MaskContour
{
    /// <summary>둘레 대비 단순화 허용 오차. OpenCV 쪽 라벨링 도구와 같은 값을 쓴다.</summary>
    public const double EpsilonRatio = 0.005;

    /// <summary>사람이 손보기 어려워지지 않도록 점 개수를 제한한다.</summary>
    public const int MaxPoints = 120;

    /// <summary>이보다 작은 덩어리는 클릭이 빗나간 것으로 본다 (마스크 픽셀 수).</summary>
    public const int MinPixelArea = 16;

    /// <summary>
    /// 조각이 이보다 많으면 물체가 아니라 얼룩으로 본다.
    /// SAM 이 내놓는 후보 중에는 화면 전체에 점이 흩뿌려진 것이 섞이는데(조각 100개 이상),
    /// 사람에게 보여 줄 만한 해석이 아니다. 실제로 나뉜 물체는 조각이 열 개를 넘지 않는다.
    /// </summary>
    public const int MaxParts = 32;

    /// <summary>
    /// SAM 디코더가 내놓은 로짓 마스크에서 도형을 뽑는다. 0 보다 크면 전경이라는 것이 SAM 규약이다.
    /// 쓸 만한 덩어리가 없으면 null.
    /// </summary>
    /// <param name="seeds">
    /// 사용자가 집은 자리 (0~1 정규화). 주면 그 점을 담고 있는 덩어리를 고른다.
    /// 없으면 가장 큰 덩어리를 고른다.
    /// </param>
    public static MaskShape? FromLogits(ReadOnlySpan<float> logits, int width, int height,
        IReadOnlyList<Labeling.NormPoint>? seeds = null, float threshold = 0f)
    {
        if (width <= 0 || height <= 0 || logits.Length < (long)width * height) return null;

        var mask = new bool[width * height];
        for (int i = 0; i < mask.Length; i++) mask[i] = logits[i] > threshold;
        return FromMask(mask, width, height, seeds);
    }

    /// <summary>
    /// 이진 마스크에서 덩어리 하나를 골라 바깥 윤곽을 뽑는다.
    ///
    /// <para>
    /// 고르는 규칙: 사용자가 집은 자리를 담고 있는 덩어리가 있으면 그것 (여럿이면 가장 큰 것),
    /// 없으면 가장 큰 덩어리. 예전에는 무조건 가장 큰 것을 골랐는데, 배경 점이 긴 물체를
    /// 두 조각으로 끊으면 사용자가 실제로 집은 조각이 작다는 이유로 버려졌다.
    /// 클릭한 자리가 결과 안에 없는 것은 사용자가 이해할 수 없는 동작이라 규칙을 바꿨다.
    /// </para>
    /// </summary>
    public static MaskShape? FromMask(bool[] mask, int width, int height,
        IReadOnlyList<Labeling.NormPoint>? seeds = null)
    {
        if (mask.Length < width * height || width <= 0 || height <= 0) return null;

        var (labels, target, area, parts) = ChooseComponent(mask, width, height, seeds);
        if (target == 0 || area < MinPixelArea) return null;

        var trace = TraceBoundary(labels, width, height, target, area);
        if (trace.Count < 3) return null;

        var simplified = SimplifyClosed(trace, EpsilonRatio * Perimeter(trace));
        // 너무 잘게 나오면 허용 오차를 키워 가며 줄인다 — 사람이 끌어 고칠 수 있는 수준으로.
        double epsilon = EpsilonRatio * Perimeter(trace);
        for (int guard = 0; simplified.Count > MaxPoints && guard < 10; guard++)
        {
            epsilon *= 1.6;
            simplified = SimplifyClosed(trace, epsilon);
        }
        if (simplified.Count < 3) return null;

        // 픽셀 중심(+0.5)을 기준으로 정규화한다. 마스크 픽셀 (0,0) 은 이미지의 왼쪽 위 '칸'이지 꼭짓점이 아니다.
        var polygon = new List<Labeling.NormPoint>(simplified.Count);
        double minX = 1, minY = 1, maxX = 0, maxY = 0;
        foreach (var (x, y) in simplified)
        {
            double nx = Math.Clamp((x + 0.5) / width, 0, 1);
            double ny = Math.Clamp((y + 0.5) / height, 0, 1);
            polygon.Add(new Labeling.NormPoint(nx, ny));
            minX = Math.Min(minX, nx); minY = Math.Min(minY, ny);
            maxX = Math.Max(maxX, nx); maxY = Math.Max(maxY, ny);
        }

        return new MaskShape(polygon, new Labeling.NormBox(minX, minY, maxX - minX, maxY - minY), area, parts);
    }

    // ───────────── 연결 요소 ─────────────

    /// <summary>
    /// 4-이웃으로 덩어리를 나눈 뒤 하나를 고른다.
    /// 대각선으로만 닿은 잡티를 끌고 오지 않도록 8-이웃이 아니라 4-이웃이다.
    /// </summary>
    private static (int[] Labels, int Target, int Area, int Parts) ChooseComponent(
        bool[] mask, int width, int height, IReadOnlyList<Labeling.NormPoint>? seeds)
    {
        var labels = new int[width * height];
        var areas = new List<int> { 0 };   // 1-기반 라벨을 그대로 색인으로 쓰려고 0번을 비워 둔다
        var stack = new Stack<int>();
        int next = 0;

        for (int start = 0; start < labels.Length; start++)
        {
            if (!mask[start] || labels[start] != 0) continue;
            int label = ++next;
            int area = 0;
            stack.Push(start);
            labels[start] = label;

            while (stack.Count > 0)
            {
                int index = stack.Pop();
                area++;
                int x = index % width, y = index / width;

                if (x > 0) Visit(index - 1);
                if (x < width - 1) Visit(index + 1);
                if (y > 0) Visit(index - width);
                if (y < height - 1) Visit(index + width);

                void Visit(int n)
                {
                    if (!mask[n] || labels[n] != 0) return;
                    labels[n] = label;
                    stack.Push(n);
                }
            }

            areas.Add(area);
        }

        if (next == 0) return (labels, 0, 0, 0);

        // 쓸 만한 크기의 덩어리만 조각으로 센다. 한두 픽셀짜리 잡티까지 세면 "조각 12개" 같은 소리가 된다.
        int parts = areas.Skip(1).Count(a => a >= MinPixelArea);

        // 사용자가 집은 자리를 담은 덩어리가 있으면 그것을 쓴다
        int seeded = SeededComponent(labels, width, height, areas, seeds);
        if (seeded != 0) return (labels, seeded, areas[seeded], Math.Max(parts, 1));

        int best = 0;   // areas[0] 은 0 이라 첫 비교에서 반드시 진다
        for (int label = 1; label < areas.Count; label++)
            if (areas[label] > areas[best]) best = label;
        return (labels, best, areas[best], Math.Max(parts, 1));
    }

    /// <summary>씨앗 점을 가장 많이 담은 덩어리. 같으면 큰 쪽. 아무 덩어리도 담지 않으면 0.</summary>
    private static int SeededComponent(int[] labels, int width, int height, List<int> areas,
        IReadOnlyList<Labeling.NormPoint>? seeds)
    {
        if (seeds is not { Count: > 0 }) return 0;

        var hits = new Dictionary<int, int>();
        foreach (var seed in seeds)
        {
            // 정규화 좌표 → 픽셀 칸. 가장자리(1.0)가 폭 밖으로 나가지 않게 잡아 둔다.
            int x = Math.Clamp((int)(seed.X * width), 0, width - 1);
            int y = Math.Clamp((int)(seed.Y * height), 0, height - 1);
            int label = labels[y * width + x];

            // 클릭이 경계에서 한두 픽셀 빗나갈 수 있다. 바로 옆까지는 같은 뜻으로 본다.
            if (label == 0) label = NearbyLabel(labels, width, height, x, y, radius: 2);
            if (label == 0) continue;
            hits[label] = hits.GetValueOrDefault(label) + 1;
        }

        int chosen = 0, chosenHits = 0;
        foreach (var (label, count) in hits)
        {
            if (areas[label] < MinPixelArea) continue;
            if (count > chosenHits || (count == chosenHits && areas[label] > areas[chosen]))
            {
                chosen = label;
                chosenHits = count;
            }
        }
        return chosen;
    }

    private static int NearbyLabel(int[] labels, int width, int height, int x, int y, int radius)
    {
        for (int r = 1; r <= radius; r++)
            for (int dy = -r; dy <= r; dy++)
                for (int dx = -r; dx <= r; dx++)
                {
                    int nx = x + dx, ny = y + dy;
                    if (nx < 0 || ny < 0 || nx >= width || ny >= height) continue;
                    int label = labels[ny * width + nx];
                    if (label != 0) return label;
                }
        return 0;
    }

    // ───────────── 경계 추적 ─────────────

    /// <summary>시계 방향 8-이웃 (화면 좌표라 y 는 아래로 증가한다)</summary>
    private static readonly (int Dx, int Dy)[] Neighbors =
        [(1, 0), (1, 1), (0, 1), (-1, 1), (-1, 0), (-1, -1), (0, -1), (1, -1)];

    /// <summary>
    /// Moore 이웃 경계 추적. 시작 픽셀로 돌아오면 끝낸다.
    /// 잘록한 모양에서는 한 바퀴를 다 돌기 전에 시작점을 다시 밟아 일부만 얻을 수 있는데,
    /// SAM 이 내놓는 덩어리에서는 실질적으로 생기지 않고, 어차피 크게 단순화하므로 그대로 둔다.
    /// 대신 픽셀 수 기준 상한을 둬서 어떤 입력에서도 반드시 끝나게 한다.
    /// </summary>
    private static List<(int X, int Y)> TraceBoundary(int[] labels, int width, int height, int target, int area)
    {
        var contour = new List<(int X, int Y)>();

        int startIndex = Array.IndexOf(labels, target);
        if (startIndex < 0) return contour;

        var start = (X: startIndex % width, Y: startIndex / width);
        var b = start;
        // 행 우선으로 처음 만난 픽셀이라 서쪽은 반드시 바깥이다
        var c = (X: start.X - 1, Y: start.Y);
        contour.Add(start);

        bool Inside(int x, int y) =>
            x >= 0 && y >= 0 && x < width && y < height && labels[y * width + x] == target;

        long limit = 4L * area + 64;
        for (long step = 0; step < limit; step++)
        {
            int from = DirectionIndex(b, c);
            (int X, int Y)? found = null;
            var previous = c;

            for (int i = 1; i <= 8; i++)
            {
                var d = Neighbors[(from + i) % 8];
                var n = (X: b.X + d.Dx, Y: b.Y + d.Dy);
                if (Inside(n.X, n.Y)) { found = n; break; }
                previous = n;   // 마지막으로 지나친 바깥 픽셀이 다음 추적의 되돌아갈 자리다
            }

            if (found is null) break;           // 외톨이 픽셀
            c = previous;
            b = found.Value;
            if (b == start) break;              // 한 바퀴
            contour.Add(b);
        }

        return contour;
    }

    private static int DirectionIndex((int X, int Y) from, (int X, int Y) to)
    {
        int dx = Math.Sign(to.X - from.X), dy = Math.Sign(to.Y - from.Y);
        for (int i = 0; i < Neighbors.Length; i++)
            if (Neighbors[i].Dx == dx && Neighbors[i].Dy == dy) return i;
        return 4;   // 같은 자리면 서쪽부터 훑는다
    }

    // ───────────── 단순화 ─────────────

    private static double Perimeter(List<(int X, int Y)> points)
    {
        double sum = 0;
        for (int i = 0; i < points.Count; i++)
        {
            var a = points[i];
            var b = points[(i + 1) % points.Count];
            sum += Math.Sqrt((a.X - b.X) * (double)(a.X - b.X) + (a.Y - b.Y) * (double)(a.Y - b.Y));
        }
        return sum;
    }

    /// <summary>
    /// 닫힌 곡선용 Douglas–Peucker. 첫 점에서 가장 먼 점을 두 번째 고정점으로 잡아
    /// 두 갈래로 나눠 각각 단순화한다. 첫 점 하나만 고정하면 그 근처가 뭉개진다.
    /// </summary>
    internal static List<(int X, int Y)> SimplifyClosed(List<(int X, int Y)> points, double epsilon)
    {
        if (points.Count <= 3) return [.. points];

        int far = 0;
        double best = -1;
        for (int i = 1; i < points.Count; i++)
        {
            double d = SquaredDistance(points[0], points[i]);
            if (d > best) { best = d; far = i; }
        }
        if (far == 0) return [.. points];

        var firstHalf = points.GetRange(0, far + 1);
        var secondHalf = points.GetRange(far, points.Count - far);
        secondHalf.Add(points[0]);

        var result = new List<(int X, int Y)>();
        var a = Simplify(firstHalf, epsilon);
        var b = Simplify(secondHalf, epsilon);
        result.AddRange(a);
        // 두 갈래가 공유하는 끝점(먼 점, 첫 점)은 한 번씩만 담는다
        for (int i = 1; i < b.Count - 1; i++) result.Add(b[i]);
        return result;
    }

    /// <summary>열린 폴리라인용 Douglas–Peucker (재귀 대신 스택 — 깊은 윤곽에서 스택 오버플로를 피한다)</summary>
    internal static List<(int X, int Y)> Simplify(List<(int X, int Y)> points, double epsilon)
    {
        if (points.Count <= 2) return [.. points];

        var keep = new bool[points.Count];
        keep[0] = keep[^1] = true;

        var ranges = new Stack<(int First, int Last)>();
        ranges.Push((0, points.Count - 1));

        while (ranges.Count > 0)
        {
            var (first, last) = ranges.Pop();
            if (last <= first + 1) continue;

            double worst = -1;
            int index = -1;
            for (int i = first + 1; i < last; i++)
            {
                double d = PerpendicularDistance(points[i], points[first], points[last]);
                if (d > worst) { worst = d; index = i; }
            }

            if (worst > epsilon && index > 0)
            {
                keep[index] = true;
                ranges.Push((first, index));
                ranges.Push((index, last));
            }
        }

        var result = new List<(int X, int Y)>();
        for (int i = 0; i < points.Count; i++)
            if (keep[i]) result.Add(points[i]);
        return result;
    }

    private static double SquaredDistance((int X, int Y) a, (int X, int Y) b)
    {
        double dx = a.X - b.X, dy = a.Y - b.Y;
        return dx * dx + dy * dy;
    }

    private static double PerpendicularDistance((int X, int Y) point, (int X, int Y) lineStart, (int X, int Y) lineEnd)
    {
        double dx = lineEnd.X - lineStart.X, dy = lineEnd.Y - lineStart.Y;
        double length = Math.Sqrt(dx * dx + dy * dy);
        if (length < 1e-9)
            return Math.Sqrt(SquaredDistance(point, lineStart));

        // 선분이 아니라 직선까지의 거리 — Douglas–Peucker 의 정의가 그렇다
        double area = Math.Abs(dx * (lineStart.Y - point.Y) - (lineStart.X - point.X) * dy);
        return area / length;
    }
}
