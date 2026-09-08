namespace BODA.VMS.MLOps.Core.Sam;

/// <summary>
/// MobileSAM 전처리 좌표 규약 (개발 문서 §5.4 SAM 보조).
///
/// <para>
/// SAM 은 긴 변을 <see cref="InputSize"/> 로 맞춘 뒤 오른쪽·아래를 0 으로 채워 정사각형으로 만든다.
/// 클릭 좌표도 같은 축척을 거쳐야 하고, 디코더가 되돌려 줄 마스크 크기도 같은 축척으로 정해진다.
/// 여기 한 곳에서만 계산해 인코더·디코더·마스크 해석이 어긋나지 않게 한다.
/// </para>
/// <para>
/// 이 프로젝트는 좌표를 0~1 정규화로 주고받는다. 그래서 디코더에 넘기는 <c>orig_im_size</c> 를
/// 원본 해상도가 아니라 축소된 크기로 준다. 마스크가 원본만큼 커지지 않아 윤곽 추적이 가벼워지고,
/// 정규화하면 결과는 같다. (2448×2048 원본이면 500만 픽셀 대신 86만 픽셀을 훑는다.)
/// </para>
/// </summary>
public readonly record struct SamGeometry
{
    /// <summary>SAM 인코더가 받는 정사각형 한 변</summary>
    public const int InputSize = 1024;

    /// <summary>긴 변을 1024 로 맞췄을 때의 너비</summary>
    public int ScaledWidth { get; }
    /// <summary>긴 변을 1024 로 맞췄을 때의 높이</summary>
    public int ScaledHeight { get; }

    private SamGeometry(int scaledWidth, int scaledHeight)
    {
        ScaledWidth = scaledWidth;
        ScaledHeight = scaledHeight;
    }

    /// <summary>원본 크기에서 축척을 정한다. 원본이 1024 보다 작아도 SAM 은 늘려서 받는다.</summary>
    public static SamGeometry For(int width, int height)
    {
        if (width <= 0 || height <= 0)
            throw new ArgumentOutOfRangeException(nameof(width), "이미지 크기는 1 이상이어야 합니다.");

        double scale = (double)InputSize / Math.Max(width, height);
        // SAM 의 ResizeLongestSide 와 같은 반올림 (int(x + 0.5))
        int w = Math.Clamp((int)(width * scale + 0.5), 1, InputSize);
        int h = Math.Clamp((int)(height * scale + 0.5), 1, InputSize);
        return new SamGeometry(w, h);
    }

    /// <summary>0~1 정규화 클릭 → 1024 패딩 공간의 픽셀 좌표 (디코더 point_coords)</summary>
    public (float X, float Y) ToInputSpace(double normalizedX, double normalizedY) =>
        ((float)(Math.Clamp(normalizedX, 0, 1) * ScaledWidth),
         (float)(Math.Clamp(normalizedY, 0, 1) * ScaledHeight));

    /// <summary>
    /// 디코더 <c>orig_im_size</c> 에 넣을 값 — [높이, 너비] 순서다.
    /// 이 값이 곧 돌려받을 마스크의 해상도가 된다.
    /// </summary>
    public (float Height, float Width) MaskSize() => (ScaledHeight, ScaledWidth);

    /// <summary>마스크 픽셀 좌표 → 0~1 정규화</summary>
    public (double X, double Y) ToNormalized(double maskX, double maskY) =>
        (Math.Clamp(maskX / ScaledWidth, 0, 1), Math.Clamp(maskY / ScaledHeight, 0, 1));
}

/// <summary>SAM 클릭 한 점. 전경(포함)과 배경(제외)을 구분한다.</summary>
public readonly record struct SamClick(double X, double Y, bool Foreground)
{
    /// <summary>디코더 point_labels 규약: 1=전경, 0=배경</summary>
    public float Label => Foreground ? 1f : 0f;
}

/// <summary>SAM 입력 정규화 상수 (원 논문의 픽셀 평균·표준편차, RGB 순서)</summary>
public static class SamNormalization
{
    public static readonly float[] Mean = [123.675f, 116.28f, 103.53f];
    public static readonly float[] Std = [58.395f, 57.12f, 57.375f];

    /// <summary>0~255 채널값을 인코더가 기대하는 값으로 옮긴다.</summary>
    public static float Apply(byte value, int channel) => (value - Mean[channel]) / Std[channel];
}
