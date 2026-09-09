namespace BODA.VMS.MLOps.Core.Inference;

/// <summary>
/// 검출 모델 입력 전처리의 좌표 규약 — 비율을 지키며 줄이고 남는 자리를 회색으로 채운다.
///
/// <para>
/// 늘려서 정사각형에 맞추면 물체가 찌그러져 학습 때와 다른 그림이 되고, 좌표를 되돌릴 때도
/// 가로세로 배율이 달라 계산이 지저분해진다. 그래서 한 배율로 줄이고 남는 자리를 채운다.
/// Ultralytics 가 쓰는 방식과 같다.
/// </para>
/// <para>
/// 여기서 하는 일은 배율과 여백을 정하고, 모델이 돌려준 좌표를 원본으로 되돌리는 것뿐이다.
/// 실제 픽셀을 옮기는 일은 이미지 라이브러리를 가진 쪽(서버)이 한다.
/// </para>
/// </summary>
public readonly record struct LetterBox
{
    private LetterBox(int inputSize, int scaledWidth, int scaledHeight, int padX, int padY, double scale)
    {
        InputSize = inputSize;
        ScaledWidth = scaledWidth;
        ScaledHeight = scaledHeight;
        PadX = padX;
        PadY = padY;
        Scale = scale;
    }

    /// <summary>모델이 받는 정사각형 한 변</summary>
    public int InputSize { get; }

    /// <summary>줄인 뒤의 크기</summary>
    public int ScaledWidth { get; }
    public int ScaledHeight { get; }

    /// <summary>왼쪽·위 여백 (가운데 정렬)</summary>
    public int PadX { get; }
    public int PadY { get; }

    /// <summary>원본 → 입력 배율</summary>
    public double Scale { get; }

    public static LetterBox For(int width, int height, int inputSize)
    {
        if (width <= 0 || height <= 0) throw new ArgumentOutOfRangeException(nameof(width), "이미지 크기는 1 이상이어야 합니다.");
        if (inputSize <= 0) throw new ArgumentOutOfRangeException(nameof(inputSize), "입력 크기는 1 이상이어야 합니다.");

        double scale = Math.Min((double)inputSize / width, (double)inputSize / height);
        int scaledWidth = Math.Max(1, (int)Math.Round(width * scale));
        int scaledHeight = Math.Max(1, (int)Math.Round(height * scale));
        int padX = (inputSize - scaledWidth) / 2;
        int padY = (inputSize - scaledHeight) / 2;
        return new LetterBox(inputSize, scaledWidth, scaledHeight, padX, padY, scale);
    }

    /// <summary>
    /// 모델 입력 좌표(0~inputSize)를 원본 기준 0~1 정규화로 되돌린다.
    /// 여백을 빼고 배율로 나눈 뒤 원본 크기로 나누는 순서다.
    /// </summary>
    public (double X, double Y) ToNormalized(double inputX, double inputY)
    {
        double x = (inputX - PadX) / Math.Max(1, ScaledWidth);
        double y = (inputY - PadY) / Math.Max(1, ScaledHeight);
        return (Math.Clamp(x, 0, 1), Math.Clamp(y, 0, 1));
    }

    /// <summary>모델 입력 좌표의 사각형(x1,y1,x2,y2)을 원본 기준 정규화 사각형으로.</summary>
    public Labeling.NormBox BoxToNormalized(double x1, double y1, double x2, double y2)
    {
        var (left, top) = ToNormalized(Math.Min(x1, x2), Math.Min(y1, y2));
        var (right, bottom) = ToNormalized(Math.Max(x1, x2), Math.Max(y1, y2));
        return new Labeling.NormBox(left, top, right - left, bottom - top);
    }
}
