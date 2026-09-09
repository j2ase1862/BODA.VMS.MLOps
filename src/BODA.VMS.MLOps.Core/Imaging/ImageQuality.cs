namespace BODA.VMS.MLOps.Core.Imaging;

/// <summary>
/// 이미지 한 장의 품질 지표 (개발 문서 §5.2 — 수집 단계 필터).
///
/// <para>
/// 두 값은 <b>서로 다른 것을 잰다</b>. 흐림은 초점·흔들림을, 노출은 밝기와 날아간 화소를 본다.
/// 하나만 보고 버리면 반대쪽으로 망가진 사진이 그대로 학습에 들어간다.
/// </para>
/// </summary>
/// <param name="Sharpness">
/// 라플라시안 분산. 클수록 또렷하다. <b>절대값에 뜻이 없다</b> — 같은 카메라·같은 배율로 찍은
/// 사진끼리 비교할 때만 쓸 수 있다 (아래 <see cref="ImageQuality"/> 설명 참고).
/// </param>
/// <param name="MeanLuma">평균 밝기 0~255.</param>
/// <param name="ClippedDarkRatio">거의 검은 화소의 비율 0~1. 노출 부족이면 커진다.</param>
/// <param name="ClippedBrightRatio">거의 흰 화소의 비율 0~1. 노출 과다면 커진다.</param>
public readonly record struct ImageQualityMetrics(
    double Sharpness,
    double MeanLuma,
    double ClippedDarkRatio,
    double ClippedBrightRatio)
{
    /// <summary>날아간 화소 비율(어두운 쪽 + 밝은 쪽). 검사 대상이 그 안에 있으면 되살릴 수 없다.</summary>
    public double ClippedRatio => ClippedDarkRatio + ClippedBrightRatio;
}

/// <summary>
/// 회색조 화소에서 흐림·노출을 잰다. 순수 계산이라 파일도 라이브러리도 건드리지 않는다.
///
/// <para><b>흐림을 절대 기준으로 쓰지 마세요.</b>
/// 라플라시안 분산은 초점뿐 아니라 <b>화면에 무엇이 찍혔는지</b>에 따라 달라진다. 결이 촘촘한 부품은
/// 흐려도 값이 크고, 매끈한 도장면은 또렷해도 값이 작다. 해상도에도 비례한다.
/// 그래서 이 값의 쓰임은 두 가지다 — 같은 라인·같은 배율의 사진들 사이에서 <b>상대적으로</b> 낮은 것을
/// 찾아내는 것, 그리고 사람이 눈으로 확인할 후보를 좁히는 것.
/// 문턱 하나를 박아 두고 자동으로 버리면 멀쩡한 샘플이 조용히 사라진다.
/// </para>
/// <para>
/// 잰 값으로 사진을 <b>지우지 않는다</b>. 흐린 NG 사진도 현장에서 실제로 일어난 일이라 데이터로서
/// 값이 있다. 지울지 말지는 사람이 정한다.
/// </para>
/// <para><b>실제 검사 사진에서 재 본 것</b> (2448×2048, 긴 변 2048 축소본 기준):</para>
/// <list type="bullet">
/// <item>원본 36.1 → 가우시안 2px 2.6 (7%) → 6px 0.7 (2%). 흐림에 크게 떨어진다.</item>
/// <item>같은 사진을 <b>원본 해상도</b>로 재면 48.7 — 축소본의 135%. 해상도만 달라도 이만큼 움직인다.</item>
/// <item>노출 부족(×0.15): 어두운 클립 0.52, 흐림도 2.4 로 떨어진다 (계조가 무너져서).</item>
/// <item>노출 과다(×2.5): 밝은 클립 0.45 인데 <b>흐림은 137.6 으로 오른다</b> —
///   날아간 화소가 딱딱한 경계를 만들기 때문이다.</item>
/// </list>
/// <para>
/// 마지막 줄이 두 지표를 함께 두는 이유다. 흐림만 보면 노출 과다가 "아주 또렷한 사진" 으로 통과한다.
/// </para>
/// </summary>
public static class ImageQuality
{
    /// <summary>이 값 이하를 "거의 검다" 로 본다. 8비트에서 5% 지점.</summary>
    public const byte DarkClip = 12;

    /// <summary>이 값 이상을 "거의 희다" 로 본다. 8비트에서 97% 지점.</summary>
    public const byte BrightClip = 247;

    /// <summary>
    /// 지표를 잰다. <paramref name="gray"/> 는 행 우선 회색조이고 길이가 width × height 여야 한다.
    /// 3×3 보다 작으면 라플라시안을 잴 수 없어 <see cref="ImageQualityMetrics.Sharpness"/> 는 0 이다.
    /// </summary>
    public static ImageQualityMetrics Measure(ReadOnlySpan<byte> gray, int width, int height)
    {
        if (width <= 0 || height <= 0 || gray.Length < width * height)
            return new ImageQualityMetrics(0, 0, 0, 0);

        long lumaSum = 0;
        long dark = 0, bright = 0;
        int pixels = width * height;

        for (int i = 0; i < pixels; i++)
        {
            byte v = gray[i];
            lumaSum += v;
            if (v <= DarkClip) dark++;
            else if (v >= BrightClip) bright++;
        }

        return new ImageQualityMetrics(
            Sharpness: LaplacianVariance(gray, width, height),
            MeanLuma: (double)lumaSum / pixels,
            ClippedDarkRatio: (double)dark / pixels,
            ClippedBrightRatio: (double)bright / pixels);
    }

    /// <summary>
    /// 4-이웃 라플라시안의 분산. 테두리 한 줄은 이웃이 모자라 건너뛴다.
    ///
    /// <para>
    /// 한 번만 훑으면서 합과 제곱합을 모은다. 큰 사진에서도 두 번 읽지 않으려는 것이고,
    /// 값 범위가 −1020~1020 이라 <c>long</c> 으로도 넘치지 않는다
    /// (1020² × 화소수 가 long 안에 들어오려면 화소가 약 8.8조 개여야 한다).
    /// </para>
    /// </summary>
    private static double LaplacianVariance(ReadOnlySpan<byte> gray, int width, int height)
    {
        if (width < 3 || height < 3) return 0;

        long sum = 0, sumSquares = 0;
        long count = (long)(width - 2) * (height - 2);

        for (int y = 1; y < height - 1; y++)
        {
            int row = y * width;
            for (int x = 1; x < width - 1; x++)
            {
                int i = row + x;
                int response = gray[i - 1] + gray[i + 1] + gray[i - width] + gray[i + width] - 4 * gray[i];
                sum += response;
                sumSquares += (long)response * response;
            }
        }

        double mean = (double)sum / count;
        double variance = (double)sumSquares / count - mean * mean;
        return variance > 0 ? variance : 0;   // 부동소수 오차로 아주 작은 음수가 나올 수 있다
    }
}
