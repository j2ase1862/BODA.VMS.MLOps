namespace BODA.VMS.MLOps.Core.Sam;

/// <summary>
/// SAM 저해상도 마스크(256×256) → 화면 좌표 마스크.
///
/// <para>
/// 디코더의 <c>masks</c> 출력은 모델이 고른 후보 하나만 후처리해 준다. 후보를 여럿 보여 주려면
/// 고르기 직전의 <c>all_low_res_masks</c> 를 받아 이 후처리를 직접 해야 한다.
/// 공식 <c>SamOnnxModel.mask_postprocessing</c> 과 같은 계산이다.
/// </para>
/// <para>
/// 256 → 1024 이중선형 확대 후 패딩을 잘라 낸다. 1024 짜리를 실제로 만들지는 않는다 —
/// 잘라 낼 영역만 바로 계산하면 되고, 그 편이 메모리도 아낀다.
/// 표본 위치는 PyTorch <c>F.interpolate(mode="bilinear", align_corners=False)</c> 의
/// 반픽셀 규칙 <c>src = (dst + 0.5) · scale − 0.5</c> 를 그대로 따른다. 이게 어긋나면
/// 마스크가 반 픽셀씩 밀려 폴리곤이 대상보다 조금씩 작거나 크게 나온다.
/// </para>
/// </summary>
public static class MaskUpscaler
{
    /// <summary>
    /// 저해상도 로짓을 목표 크기로 확대한다. 목표 크기는 <see cref="SamGeometry"/> 가 정한 축소본 크기다.
    /// </summary>
    /// <param name="lowRes">저해상도 로짓 (lowSize × lowSize, 행 우선)</param>
    /// <param name="lowSize">저해상도 한 변 (SAM 은 256)</param>
    /// <param name="geometry">이 이미지의 축척</param>
    public static float[] Expand(ReadOnlySpan<float> lowRes, int lowSize, SamGeometry geometry)
    {
        if (lowSize <= 0) throw new ArgumentOutOfRangeException(nameof(lowSize));
        if (lowRes.Length < lowSize * lowSize)
            throw new ArgumentException("저해상도 마스크가 모자랍니다.", nameof(lowRes));

        int width = geometry.ScaledWidth, height = geometry.ScaledHeight;
        var result = new float[width * height];

        // 저해상도 256 칸이 1024 정사각형 전체를 덮는다. 잘라 낼 곳은 그 왼쪽 위 귀퉁이다.
        double scale = (double)lowSize / SamGeometry.InputSize;

        // 가로 표본 위치를 미리 구해 둔다 (행마다 다시 계산할 이유가 없다)
        var columnLeft = new int[width];
        var columnRight = new int[width];
        var columnWeight = new float[width];
        for (int x = 0; x < width; x++)
        {
            Sample(x, scale, lowSize, out columnLeft[x], out columnRight[x], out columnWeight[x]);
        }

        for (int y = 0; y < height; y++)
        {
            Sample(y, scale, lowSize, out int top, out int bottom, out float wy);
            int topRow = top * lowSize, bottomRow = bottom * lowSize;
            int destinationRow = y * width;

            for (int x = 0; x < width; x++)
            {
                int left = columnLeft[x], right = columnRight[x];
                float wx = columnWeight[x];

                float topValue = lowRes[topRow + left] + (lowRes[topRow + right] - lowRes[topRow + left]) * wx;
                float bottomValue = lowRes[bottomRow + left] + (lowRes[bottomRow + right] - lowRes[bottomRow + left]) * wx;
                result[destinationRow + x] = topValue + (bottomValue - topValue) * wy;
            }
        }

        return result;
    }

    /// <summary>반픽셀 규칙으로 이웃 두 칸과 가중치를 고른다.</summary>
    private static void Sample(int destination, double scale, int lowSize, out int first, out int second, out float weight)
    {
        double source = (destination + 0.5) * scale - 0.5;
        if (source < 0) source = 0;                      // 가장자리는 바깥으로 나가지 않게 잡아 둔다
        if (source > lowSize - 1) source = lowSize - 1;

        first = (int)Math.Floor(source);
        second = Math.Min(first + 1, lowSize - 1);
        weight = (float)(source - first);
    }
}
