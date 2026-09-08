namespace BODA.VMS.MLOps.Core.Hashing;

/// <summary>
/// dHash — 인접 픽셀의 밝기 대소만 보는 64비트 지각 해시 (개발 문서 §5.2 중복 제거).
/// SHA-256 이 "같은 파일"을 잡는다면 이것은 "거의 같은 사진"을 잡는다.
/// 리사이즈·재인코딩·약한 밝기 변화에는 값이 거의 그대로다.
///
/// 입력은 9×8 회색조 픽셀이다. 리사이즈는 이미지 라이브러리가 있는 서버에서 하고,
/// 여기는 순수 계산만 남겨 시험할 수 있게 한다.
/// </summary>
public static class PerceptualHash
{
    public const int SampleWidth = 9;
    public const int SampleHeight = 8;

    /// <summary>9×8 회색조(행 우선)에서 64비트 해시를 만든다</summary>
    public static ulong FromGrayscale9x8(ReadOnlySpan<byte> gray)
    {
        if (gray.Length != SampleWidth * SampleHeight)
            throw new ArgumentException($"{SampleWidth}×{SampleHeight} = {SampleWidth * SampleHeight} 바이트가 필요합니다.", nameof(gray));

        ulong hash = 0;
        int bit = 0;
        for (int y = 0; y < SampleHeight; y++)
        {
            int row = y * SampleWidth;
            for (int x = 0; x < SampleWidth - 1; x++)
            {
                if (gray[row + x] > gray[row + x + 1]) hash |= 1UL << bit;
                bit++;
            }
        }
        return hash;
    }

    /// <summary>서로 다른 비트 수. 0 이면 사실상 같은 사진, 5 이하면 거의 같다고 본다.</summary>
    public static int Distance(ulong a, ulong b) => System.Numerics.BitOperations.PopCount(a ^ b);

    /// <summary>이 값 이하를 "거의 같은 사진" 으로 본다. 실사용에서 오탐과 미탐이 균형을 이루는 지점.</summary>
    public const int NearDuplicateThreshold = 5;

    public static bool IsNearDuplicate(ulong a, ulong b) => Distance(a, b) <= NearDuplicateThreshold;

    /// <summary>DB 에 넣을 16자리 16진 문자열</summary>
    public static string ToHex(ulong hash) => hash.ToString("x16");

    public static ulong FromHex(string? hex) =>
        !string.IsNullOrWhiteSpace(hex) && ulong.TryParse(hex, System.Globalization.NumberStyles.HexNumber,
            System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : 0;
}
