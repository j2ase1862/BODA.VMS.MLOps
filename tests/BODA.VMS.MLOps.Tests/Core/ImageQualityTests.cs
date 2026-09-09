using BODA.VMS.MLOps.Core.Imaging;
using FluentAssertions;

namespace BODA.VMS.MLOps.Tests.Core;

/// <summary>
/// 흐림·노출 지표.
///
/// <para>
/// 지표가 잘못 나오면 "그럴듯한" 숫자가 화면에 붙고, 사람은 그 숫자를 믿고 사진을 버린다.
/// 그래서 값 자체보다 <b>순서</b>를 못 박는다 — 흐린 것이 또렷한 것보다 낮게, 어두운 것이
/// 어두운 쪽 클리핑이 크게 나오는지.
/// </para>
/// </summary>
public class ImageQualityTests
{
    /// <summary>바둑판 무늬 — 이웃 화소가 매번 뒤집혀 라플라시안이 가장 크게 나온다.</summary>
    private static byte[] Checkerboard(int w, int h, int cell = 1)
    {
        var gray = new byte[w * h];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                gray[y * w + x] = ((x / cell + y / cell) % 2 == 0) ? (byte)0 : (byte)255;
        return gray;
    }

    /// <summary>같은 무늬를 3×3 평균으로 한 번 뭉갠 것 — 초점이 나간 사진에 해당한다.</summary>
    private static byte[] Blurred(byte[] source, int w, int h)
    {
        var output = new byte[source.Length];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int sum = 0, n = 0;
                for (int dy = -1; dy <= 1; dy++)
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        int nx = x + dx, ny = y + dy;
                        if (nx < 0 || ny < 0 || nx >= w || ny >= h) continue;
                        sum += source[ny * w + nx];
                        n++;
                    }
                output[y * w + x] = (byte)(sum / n);
            }
        return output;
    }

    private static byte[] Flat(int w, int h, byte value) => Enumerable.Repeat(value, w * h).ToArray();

    [Fact]
    public void Blurred_image_scores_lower_than_the_sharp_one()
    {
        const int w = 64, h = 64;
        var sharp = Checkerboard(w, h, cell: 2);
        var blurred = Blurred(sharp, w, h);

        var sharpMetrics = ImageQuality.Measure(sharp, w, h);
        var blurredMetrics = ImageQuality.Measure(blurred, w, h);

        blurredMetrics.Sharpness.Should().BeLessThan(sharpMetrics.Sharpness,
            "뭉갠 사진이 더 또렷하다고 나오면 필터가 거꾸로 돈다");
        sharpMetrics.Sharpness.Should().BeGreaterThan(0);
    }

    /// <summary>무늬가 없는 면은 초점과 무관하게 0 이다 — 이 값을 절대 기준으로 쓰면 안 되는 이유다.</summary>
    [Fact]
    public void Flat_image_has_no_sharpness()
        => ImageQuality.Measure(Flat(32, 32, 128), 32, 32).Sharpness.Should().Be(0);

    [Fact]
    public void Mean_luma_follows_the_pixels()
    {
        ImageQuality.Measure(Flat(16, 16, 0), 16, 16).MeanLuma.Should().Be(0);
        ImageQuality.Measure(Flat(16, 16, 128), 16, 16).MeanLuma.Should().Be(128);
        ImageQuality.Measure(Flat(16, 16, 255), 16, 16).MeanLuma.Should().Be(255);
    }

    /// <summary>노출 부족은 어두운 쪽이, 과다는 밝은 쪽이 날아간다. 둘을 나눠 세야 어느 쪽인지 안다.</summary>
    [Fact]
    public void Clipping_is_counted_on_each_side_separately()
    {
        var underexposed = ImageQuality.Measure(Flat(16, 16, 3), 16, 16);
        underexposed.ClippedDarkRatio.Should().Be(1.0);
        underexposed.ClippedBrightRatio.Should().Be(0.0);

        var overexposed = ImageQuality.Measure(Flat(16, 16, 252), 16, 16);
        overexposed.ClippedDarkRatio.Should().Be(0.0);
        overexposed.ClippedBrightRatio.Should().Be(1.0);

        var wellExposed = ImageQuality.Measure(Flat(16, 16, 128), 16, 16);
        wellExposed.ClippedRatio.Should().Be(0.0);
    }

    /// <summary>절반만 날아간 경우도 비율로 나와야 한다 — 검사 대상이 그 절반에 있을 수 있다.</summary>
    [Fact]
    public void Partially_clipped_image_reports_the_fraction()
    {
        const int w = 20, h = 10;
        var gray = new byte[w * h];
        for (int i = 0; i < gray.Length; i++) gray[i] = i < gray.Length / 2 ? (byte)0 : (byte)128;

        var metrics = ImageQuality.Measure(gray, w, h);

        metrics.ClippedDarkRatio.Should().BeApproximately(0.5, 1e-9);
        metrics.ClippedBrightRatio.Should().Be(0.0);
    }

    /// <summary>잴 수 없는 입력에 예외를 던지지 않는다 — 업로드 한 장 때문에 배치가 죽으면 안 된다.</summary>
    [Theory]
    [InlineData(0, 0)]
    [InlineData(2, 2)]     // 3×3 보다 작아 라플라시안을 못 잰다
    [InlineData(-1, 5)]
    public void Degenerate_sizes_return_zero_instead_of_throwing(int w, int h)
    {
        var gray = new byte[Math.Max(0, w) * Math.Max(0, h)];

        var metrics = ImageQuality.Measure(gray, w, h);

        metrics.Sharpness.Should().Be(0);
    }

    /// <summary>길이가 모자라면 잘못 읽는 대신 0 을 준다.</summary>
    [Fact]
    public void Short_buffer_returns_zero()
        => ImageQuality.Measure(new byte[10], 100, 100).Sharpness.Should().Be(0);

    /// <summary>
    /// 분산은 음수가 될 수 없다. 합·제곱합을 한 번에 모으는 식은 부동소수 오차로
    /// 아주 작은 음수를 낼 수 있어 막아 두었다.
    /// </summary>
    [Fact]
    public void Variance_is_never_negative()
    {
        for (byte value = 0; value < 255; value += 51)
            ImageQuality.Measure(Flat(9, 9, value), 9, 9).Sharpness.Should().BeGreaterThanOrEqualTo(0);
    }
}
