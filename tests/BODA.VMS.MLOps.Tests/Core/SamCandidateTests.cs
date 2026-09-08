using BODA.VMS.MLOps.Core.Labeling;
using BODA.VMS.MLOps.Core.Sam;
using FluentAssertions;

namespace BODA.VMS.MLOps.Tests.Core;

/// <summary>
/// 클릭한 자리를 담은 덩어리를 고르는 규칙.
/// 예전에는 무조건 가장 큰 덩어리를 골라서, 배경 점이 긴 물체를 끊으면
/// 사용자가 집은 조각이 작다는 이유로 버려졌다. 결과에 클릭한 점이 없는 셈이었다.
/// </summary>
public class SeededComponentTests
{
    /// <summary>왼쪽에 큰 사각형, 오른쪽에 작은 사각형. 둘은 떨어져 있다.</summary>
    private static bool[] TwoRectangles(int width, int height)
    {
        var mask = new bool[width * height];
        for (int y = 40; y < 120; y++)
            for (int x = 10; x < 90; x++)
                mask[y * width + x] = true;        // 큰 것 80×80
        for (int y = 60; y < 90; y++)
            for (int x = 150; x < 180; x++)
                mask[y * width + x] = true;        // 작은 것 30×30
        return mask;
    }

    [Fact]
    public void 씨앗이_없으면_예전처럼_큰_덩어리를_고른다()
    {
        var mask = TwoRectangles(200, 160);

        var shape = MaskContour.FromMask(mask, 200, 160);

        shape!.PixelArea.Should().Be(80 * 80);
    }

    [Fact]
    public void 작은_덩어리를_집으면_그것을_돌려준다()
    {
        var mask = TwoRectangles(200, 160);
        // 작은 사각형 한가운데 (165, 75)
        List<NormPoint> seeds = [new(165.0 / 200, 75.0 / 160)];

        var shape = MaskContour.FromMask(mask, 200, 160, seeds);

        shape!.PixelArea.Should().Be(30 * 30, "사용자가 집은 조각이 작다고 버리면 안 된다");
    }

    [Fact]
    public void 돌려준_폴리곤_안에_클릭한_점이_들어_있다()
    {
        var mask = TwoRectangles(200, 160);
        var seed = new NormPoint(165.0 / 200, 75.0 / 160);

        var shape = MaskContour.FromMask(mask, 200, 160, [seed]);

        // 사각형이라 외접 박스만 봐도 충분하다
        var box = shape!.Box;
        seed.X.Should().BeInRange(box.X, box.X + box.Width);
        seed.Y.Should().BeInRange(box.Y, box.Y + box.Height);
    }

    [Fact]
    public void 조각이_몇_개인지_알려_준다()
    {
        var mask = TwoRectangles(200, 160);

        var shape = MaskContour.FromMask(mask, 200, 160);

        shape!.PartCount.Should().Be(2, "화면이 '조각이 더 있다' 고 알려 줄 수 있어야 한다");
    }

    [Fact]
    public void 씨앗이_여러_개면_많이_담은_덩어리를_고른다()
    {
        var mask = TwoRectangles(200, 160);
        List<NormPoint> seeds = [
            new(165.0 / 200, 70.0 / 160),   // 작은 것
            new(170.0 / 200, 80.0 / 160),   // 작은 것
            new(50.0 / 200, 80.0 / 160),    // 큰 것
        ];

        var shape = MaskContour.FromMask(mask, 200, 160, seeds);

        shape!.PixelArea.Should().Be(30 * 30);
    }

    [Fact]
    public void 씨앗이_아무_덩어리에도_안_닿으면_큰_덩어리로_돌아간다()
    {
        var mask = TwoRectangles(200, 160);
        List<NormPoint> seeds = [new(0.99, 0.99)];   // 빈 곳

        var shape = MaskContour.FromMask(mask, 200, 160, seeds);

        shape!.PixelArea.Should().Be(80 * 80);
    }

    [Fact]
    public void 경계에서_한두_픽셀_빗나간_클릭도_받아_준다()
    {
        var mask = TwoRectangles(200, 160);
        // 작은 사각형 왼쪽 경계(x=150) 바로 바깥인 149
        List<NormPoint> seeds = [new(149.0 / 200, 75.0 / 160)];

        var shape = MaskContour.FromMask(mask, 200, 160, seeds);

        shape!.PixelArea.Should().Be(30 * 30);
    }

    [Fact]
    public void 잡티_크기_덩어리는_씨앗이_있어도_고르지_않는다()
    {
        var mask = new bool[100 * 100];
        for (int y = 40; y < 80; y++)
            for (int x = 40; x < 80; x++)
                mask[y * 100 + x] = true;
        mask[5 * 100 + 5] = true;   // 1 px 잡티

        var shape = MaskContour.FromMask(mask, 100, 100, [new NormPoint(0.055, 0.055)]);

        shape!.PixelArea.Should().Be(40 * 40);
        shape.PartCount.Should().Be(1, "1 px 잡티를 조각으로 세면 안 된다");
    }
}

/// <summary>
/// 저해상도(256) 마스크를 화면 크기로 올리는 계산.
/// 반픽셀 규칙이 어긋나면 폴리곤이 대상보다 조금씩 밀리거나 커진다.
/// </summary>
public class MaskUpscalerTests
{
    [Fact]
    public void 크기가_축소본과_같다()
    {
        var geometry = SamGeometry.For(2048, 1024);   // → 1024 × 512
        var lowRes = new float[256 * 256];

        var expanded = MaskUpscaler.Expand(lowRes, 256, geometry);

        expanded.Length.Should().Be(1024 * 512);
    }

    [Fact]
    public void 값이_한결같으면_확대해도_한결같다()
    {
        var geometry = SamGeometry.For(800, 600);
        var lowRes = new float[256 * 256];
        Array.Fill(lowRes, 2.5f);

        var expanded = MaskUpscaler.Expand(lowRes, 256, geometry);

        expanded.Should().OnlyContain(v => Math.Abs(v - 2.5f) < 1e-5f);
    }

    [Fact]
    public void 저해상도의_전경_사각형이_같은_자리로_커진다()
    {
        // 저해상도에서 가운데 절반이 전경이면, 확대해도 가운데 절반이어야 한다.
        // 정사각형 이미지라 축소본이 1024×1024 이고 패딩이 없다.
        var geometry = SamGeometry.For(1000, 1000);
        var lowRes = new float[256 * 256];
        Array.Fill(lowRes, -5f);
        for (int y = 64; y < 192; y++)
            for (int x = 64; x < 192; x++)
                lowRes[y * 256 + x] = 5f;

        var expanded = MaskUpscaler.Expand(lowRes, 256, geometry);
        var shape = MaskContour.FromLogits(expanded, geometry.ScaledWidth, geometry.ScaledHeight);

        shape.Should().NotBeNull();
        shape!.Box.X.Should().BeApproximately(0.25, 0.01);
        shape.Box.Y.Should().BeApproximately(0.25, 0.01);
        shape.Box.Width.Should().BeApproximately(0.5, 0.02);
        shape.Box.Height.Should().BeApproximately(0.5, 0.02);
    }

    [Fact]
    public void 세로로_긴_이미지도_비율이_유지된다()
    {
        // 축소본이 512×1024 면 오른쪽 절반은 패딩이라 잘려 나가야 한다.
        // 저해상도 오른쪽 절반에 전경을 넣어도 결과에는 나오지 않아야 맞다.
        var geometry = SamGeometry.For(500, 1000);
        geometry.ScaledWidth.Should().Be(512);
        geometry.ScaledHeight.Should().Be(1024);

        var lowRes = new float[256 * 256];
        Array.Fill(lowRes, -5f);
        for (int y = 0; y < 256; y++)
            for (int x = 160; x < 256; x++)   // 1024 공간에서 640..1024 — 전부 패딩 자리
                lowRes[y * 256 + x] = 5f;

        var expanded = MaskUpscaler.Expand(lowRes, 256, geometry);

        expanded.Should().OnlyContain(v => v < 0, "패딩 영역은 잘려 나가야 한다");
    }

    [Fact]
    public void 왼쪽_위_귀퉁이를_잘라_온다()
    {
        var geometry = SamGeometry.For(500, 1000);   // 512 × 1024
        var lowRes = new float[256 * 256];
        Array.Fill(lowRes, -5f);
        // 1024 공간의 왼쪽 위 128×128 → 저해상도 32×32
        for (int y = 0; y < 32; y++)
            for (int x = 0; x < 32; x++)
                lowRes[y * 256 + x] = 5f;

        var expanded = MaskUpscaler.Expand(lowRes, 256, geometry);
        var shape = MaskContour.FromLogits(expanded, geometry.ScaledWidth, geometry.ScaledHeight);

        shape.Should().NotBeNull();
        shape!.Box.X.Should().BeApproximately(0, 0.01);
        shape.Box.Y.Should().BeApproximately(0, 0.01);
        // 128 / 512 = 0.25 (가로), 128 / 1024 = 0.125 (세로)
        shape.Box.Width.Should().BeApproximately(0.25, 0.02);
        shape.Box.Height.Should().BeApproximately(0.125, 0.02);
    }

    [Fact]
    public void 저해상도가_모자라면_거부한다()
    {
        var geometry = SamGeometry.For(100, 100);
        var act = () => MaskUpscaler.Expand(new float[10], 256, geometry);
        act.Should().Throw<ArgumentException>();
    }
}
