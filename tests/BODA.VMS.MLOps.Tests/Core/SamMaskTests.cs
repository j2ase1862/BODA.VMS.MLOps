using BODA.VMS.MLOps.Core.Sam;
using FluentAssertions;

namespace BODA.VMS.MLOps.Tests.Core;

/// <summary>
/// SAM 보조 라벨링의 순수 부분 — 좌표 규약과 마스크→폴리곤 변환.
/// ONNX 모델 없이도 여기서 어긋나면 캔버스에 엉뚱한 폴리곤이 붙는다.
/// </summary>
public class SamGeometryTests
{
    [Theory]
    [InlineData(2048, 1024, 1024, 512)]
    [InlineData(1024, 2048, 512, 1024)]
    [InlineData(1000, 1000, 1024, 1024)]   // 작아도 늘려서 넣는다
    [InlineData(2448, 2048, 1024, 857)]
    public void 긴_변을_1024로_맞춘다(int width, int height, int expectedWidth, int expectedHeight)
    {
        var geometry = SamGeometry.For(width, height);
        geometry.ScaledWidth.Should().Be(expectedWidth);
        geometry.ScaledHeight.Should().Be(expectedHeight);
    }

    [Fact]
    public void 축척을_해도_가로세로_비율이_유지된다()
    {
        var geometry = SamGeometry.For(2448, 2048);
        double original = 2448.0 / 2048.0;
        double scaled = (double)geometry.ScaledWidth / geometry.ScaledHeight;
        scaled.Should().BeApproximately(original, 0.005);
    }

    [Fact]
    public void 정규화_클릭이_1024_공간의_픽셀로_간다()
    {
        var geometry = SamGeometry.For(2048, 1024);   // → 1024 × 512

        geometry.ToInputSpace(0, 0).Should().Be((0f, 0f));
        geometry.ToInputSpace(1, 1).Should().Be((1024f, 512f));
        geometry.ToInputSpace(0.5, 0.5).Should().Be((512f, 256f));
    }

    [Fact]
    public void 범위를_벗어난_클릭은_잘린다()
    {
        var geometry = SamGeometry.For(1000, 1000);
        geometry.ToInputSpace(-3, 7).Should().Be((0f, 1024f));
    }

    [Fact]
    public void 마스크_크기는_높이_너비_순서다()
    {
        // 디코더의 orig_im_size 는 [H, W] 다. 뒤집으면 마스크가 90도 돌아간 것처럼 나온다.
        var geometry = SamGeometry.For(2048, 1024);
        geometry.MaskSize().Should().Be((512f, 1024f));
    }

    [Fact]
    public void 마스크_좌표를_되돌리면_정규화_값이_나온다()
    {
        var geometry = SamGeometry.For(2048, 1024);
        var (x, y) = geometry.ToNormalized(512, 128);
        x.Should().BeApproximately(0.5, 1e-9);
        y.Should().BeApproximately(0.25, 1e-9);
    }

    [Fact]
    public void 크기가_0이면_거부한다()
    {
        var act = () => SamGeometry.For(0, 100);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void 전경과_배경_라벨은_1과_0이다()
    {
        new SamClick(0.5, 0.5, Foreground: true).Label.Should().Be(1f);
        new SamClick(0.5, 0.5, Foreground: false).Label.Should().Be(0f);
    }
}

public class MaskContourTests
{
    /// <summary>지정한 사각형만 전경인 마스크를 만든다.</summary>
    private static bool[] Rectangle(int width, int height, int left, int top, int right, int bottom)
    {
        var mask = new bool[width * height];
        for (int y = top; y < bottom; y++)
            for (int x = left; x < right; x++)
                mask[y * width + x] = true;
        return mask;
    }

    [Fact]
    public void 사각형_마스크가_사각형_박스로_돌아온다()
    {
        var mask = Rectangle(100, 100, 20, 30, 60, 80);

        var shape = MaskContour.FromMask(mask, 100, 100);

        shape.Should().NotBeNull();
        shape!.PixelArea.Should().Be(40 * 50);
        shape.Box.X.Should().BeApproximately(0.205, 0.01);
        shape.Box.Y.Should().BeApproximately(0.305, 0.01);
        shape.Box.Width.Should().BeApproximately(0.39, 0.02);
        shape.Box.Height.Should().BeApproximately(0.49, 0.02);
    }

    [Fact]
    public void 사각형은_네_점으로_단순화된다()
    {
        var mask = Rectangle(200, 200, 40, 40, 160, 160);

        var shape = MaskContour.FromMask(mask, 200, 200);

        shape.Should().NotBeNull();
        // 모서리 처리에 따라 하나쯤 더 남을 수 있어 여유를 둔다
        shape!.Polygon.Count.Should().BeInRange(4, 6);
    }

    [Fact]
    public void 폴리곤_점이_모두_0과_1_사이다()
    {
        var mask = Rectangle(64, 64, 0, 0, 64, 64);   // 가장자리에 딱 붙은 덩어리

        var shape = MaskContour.FromMask(mask, 64, 64);

        shape.Should().NotBeNull();
        shape!.Polygon.Should().OnlyContain(p => p.X >= 0 && p.X <= 1 && p.Y >= 0 && p.Y <= 1);
    }

    [Fact]
    public void 덩어리가_둘이면_큰_쪽만_남는다()
    {
        var mask = Rectangle(100, 100, 5, 5, 15, 15);          // 작은 것 (100 px)
        var big = Rectangle(100, 100, 40, 40, 90, 90);         // 큰 것 (2500 px)
        for (int i = 0; i < mask.Length; i++) mask[i] |= big[i];

        var shape = MaskContour.FromMask(mask, 100, 100);

        shape.Should().NotBeNull();
        shape!.PixelArea.Should().Be(50 * 50);
        shape.Box.X.Should().BeGreaterThan(0.3);   // 작은 덩어리를 끌고 오지 않았다
    }

    [Fact]
    public void 대각선으로만_닿은_잡티는_따로_센다()
    {
        // (10,10) 블록과 (20,20) 블록이 한 점에서만 만나면 다른 덩어리로 봐야 한다
        var mask = new bool[40 * 40];
        for (int y = 5; y < 15; y++) for (int x = 5; x < 15; x++) mask[y * 40 + x] = true;
        mask[15 * 40 + 15] = true;   // 모서리 하나로만 이어진 점

        var shape = MaskContour.FromMask(mask, 40, 40);

        shape.Should().NotBeNull();
        shape!.PixelArea.Should().Be(100);
    }

    [Fact]
    public void 빈_마스크는_null이다()
    {
        MaskContour.FromMask(new bool[100 * 100], 100, 100).Should().BeNull();
    }

    [Fact]
    public void 너무_작은_덩어리는_클릭_실패로_본다()
    {
        var mask = Rectangle(100, 100, 10, 10, 12, 12);   // 4 px
        MaskContour.FromMask(mask, 100, 100).Should().BeNull();
    }

    [Fact]
    public void 로짓은_0보다_클_때만_전경이다()
    {
        var logits = new float[16 * 16];
        for (int y = 4; y < 12; y++)
            for (int x = 4; x < 12; x++)
                logits[y * 16 + x] = 3.5f;
        for (int i = 0; i < logits.Length; i++)
            if (logits[i] == 0) logits[i] = -2.5f;

        var shape = MaskContour.FromLogits(logits, 16, 16);

        shape.Should().NotBeNull();
        shape!.PixelArea.Should().Be(64);
    }

    [Fact]
    public void 로짓_길이가_모자라면_null이다()
    {
        MaskContour.FromLogits(new float[10], 16, 16).Should().BeNull();
    }

    [Fact]
    public void 원형_마스크는_점_상한을_넘지_않는다()
    {
        const int size = 512;
        var mask = new bool[size * size];
        double cx = size / 2.0, cy = size / 2.0, r = size / 2.0 - 4;
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
                if ((x - cx) * (x - cx) + (y - cy) * (y - cy) <= r * r)
                    mask[y * size + x] = true;

        var shape = MaskContour.FromMask(mask, size, size);

        shape.Should().NotBeNull();
        shape!.Polygon.Count.Should().BeLessThanOrEqualTo(MaskContour.MaxPoints);
        shape.Polygon.Count.Should().BeGreaterThanOrEqualTo(6);   // 원이 삼각형으로 뭉개지지는 않는다
        // 원의 외접 박스는 이미지를 거의 다 덮는다
        shape.Box.Width.Should().BeGreaterThan(0.9);
        shape.Box.Height.Should().BeGreaterThan(0.9);
    }

    [Fact]
    public void 폴리곤_넓이가_실제_덩어리와_크게_다르지_않다()
    {
        // 단순화가 도형을 망가뜨리지 않는지 — 신발끈 공식으로 넓이를 비교한다
        const int size = 256;
        var mask = new bool[size * size];
        for (int y = 40; y < 200; y++)
            for (int x = 60; x < 180; x++)
                mask[y * size + x] = true;

        var shape = MaskContour.FromMask(mask, size, size)!;

        double area = 0;
        var points = shape.Polygon;
        for (int i = 0; i < points.Count; i++)
        {
            var a = points[i];
            var b = points[(i + 1) % points.Count];
            area += a.X * b.Y - b.X * a.Y;
        }
        area = Math.Abs(area) / 2;

        double expected = (160.0 * 120.0) / (size * (double)size);
        area.Should().BeApproximately(expected, expected * 0.05);
    }

    [Fact]
    public void 가늘고_긴_모양도_추적이_끝난다()
    {
        // 종료 조건이 없으면 여기서 멈추지 않는다 — 상한이 실제로 걸리는지 본다
        var mask = new bool[200 * 200];
        for (int y = 20; y < 180; y++) mask[y * 200 + 100] = true;          // 세로 1 px 선
        for (int x = 20; x < 180; x++) mask[100 * 200 + x] = true;          // 가로 1 px 선

        var shape = MaskContour.FromMask(mask, 200, 200);

        shape.Should().NotBeNull();
        shape!.Polygon.Count.Should().BeGreaterThanOrEqualTo(3);
    }

    [Fact]
    public void 단순화가_양_끝점을_지키다()
    {
        List<(int X, int Y)> line = [(0, 0), (5, 1), (10, 0), (15, 1), (20, 0)];

        var simplified = MaskContour.Simplify(line, epsilon: 2);

        simplified.Should().HaveCount(2);
        simplified[0].Should().Be((0, 0));
        simplified[^1].Should().Be((20, 0));
    }

    [Fact]
    public void 단순화_오차보다_큰_꺾임은_남긴다()
    {
        List<(int X, int Y)> line = [(0, 0), (10, 30), (20, 0)];

        var simplified = MaskContour.Simplify(line, epsilon: 2);

        simplified.Should().HaveCount(3);
    }
}
