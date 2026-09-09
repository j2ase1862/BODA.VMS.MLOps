using BODA.VMS.MLOps.Core.Hashing;
using BODA.VMS.MLOps.Core.Imaging;
using SkiaSharp;

namespace BODA.VMS.MLOps.Server.Services;

/// <summary>업로드된 이미지 한 장에서 뽑아낸 것들</summary>
public sealed record ProcessedImage(
    int Width,
    int Height,
    string PerceptualHash,
    byte[] Thumbnail,
    /// <summary>캔버스용 축소본. 원본이 이미 작으면 null 이고, 그때는 원본을 그대로 쓴다.</summary>
    byte[]? View,
    /// <summary>흐림·노출 지표. 사람이 걸러 볼 후보를 좁히는 데 쓴다 (자동으로 버리지 않는다).</summary>
    ImageQualityMetrics Quality);

/// <summary>
/// 이미지 디코딩·축소본·지각 해시 (개발 문서 §5.2·§5.4).
///
/// <para>
/// 타일 피라미드는 두지 않았다. 문서가 상정한 2448×2048 급은 JPEG 축소본 한 장이면
/// 화면 전체를 덮고, 확대할 때만 원본을 쓰면 된다. 타일은 저장 용량과 생성 비용이 큰 대신
/// 이 해상도에서는 이득이 없다. 훨씬 큰 이미지가 들어오면 그때 <see cref="ViewMaxEdge"/> 위에 타일을 얹는다.
/// </para>
/// <para>
/// 원본은 재인코딩하지 않고 그대로 저장한다 — 검사 재현성 때문이다 (문서 §5.2).
/// </para>
/// </summary>
public sealed class ImageProcessor(ILogger<ImageProcessor> logger)
{
    /// <summary>목록 격자에 쓰는 썸네일의 긴 변</summary>
    public const int ThumbnailMaxEdge = 320;
    /// <summary>라벨링 캔버스가 처음 받는 축소본의 긴 변</summary>
    public const int ViewMaxEdge = 2048;

    private const int ThumbnailQuality = 80;
    private const int ViewQuality = 88;

    public sealed class UnsupportedImageException(string message) : Exception(message);

    /// <summary>
    /// 원본 파일을 읽어 크기·해시·축소본을 만든다. 디코딩할 수 없으면 <see cref="UnsupportedImageException"/>.
    /// </summary>
    public ProcessedImage Process(string sourcePath)
    {
        using var codec = SKCodec.Create(sourcePath)
            ?? throw new UnsupportedImageException("이미지로 읽을 수 없는 파일입니다 (지원 형식: JPEG·PNG·BMP·WEBP·GIF).");

        var info = codec.Info;
        if (info.Width <= 0 || info.Height <= 0)
            throw new UnsupportedImageException("이미지 크기를 읽지 못했습니다.");

        // 원본이 아주 크면 디코딩 단계에서 미리 줄여 메모리를 아낀다
        var scaled = codec.GetScaledDimensions(ScaleFor(info.Width, info.Height, ViewMaxEdge));
        using var bitmap = SKBitmap.Decode(codec, new SKImageInfo(scaled.Width, scaled.Height, SKColorType.Rgba8888, SKAlphaType.Premul))
            ?? throw new UnsupportedImageException("이미지를 디코딩하지 못했습니다.");

        var thumbnail = Encode(bitmap, ThumbnailMaxEdge, ThumbnailQuality)
            ?? throw new UnsupportedImageException("썸네일을 만들지 못했습니다.");

        // 원본이 이미 축소본보다 작으면 축소본을 따로 두지 않는다
        byte[]? view = Math.Max(info.Width, info.Height) > ViewMaxEdge
            ? Encode(bitmap, ViewMaxEdge, ViewQuality)
            : null;

        var phash = PerceptualHash.ToHex(ComputeDHash(bitmap));
        var quality = MeasureQuality(bitmap);
        return new ProcessedImage(info.Width, info.Height, phash, thumbnail, view, quality);
    }

    /// <summary>
    /// OCR 학습은 텍스트 영역만 잘라 쓴다. 정규화 사각형을 원본에서 잘라 JPEG 으로 낸다.
    /// 잘라낼 수 없으면 null.
    /// </summary>
    public byte[]? Crop(string sourcePath, double x, double y, double w, double h)
    {
        try
        {
            using var bitmap = SKBitmap.Decode(sourcePath);
            if (bitmap is null) return null;

            var rect = SKRectI.Create(
                (int)Math.Round(x * bitmap.Width),
                (int)Math.Round(y * bitmap.Height),
                Math.Max(1, (int)Math.Round(w * bitmap.Width)),
                Math.Max(1, (int)Math.Round(h * bitmap.Height)));
            rect = SKRectI.Intersect(rect, SKRectI.Create(0, 0, bitmap.Width, bitmap.Height));
            if (rect.Width <= 0 || rect.Height <= 0) return null;

            using var cropped = new SKBitmap(rect.Width, rect.Height);
            if (!bitmap.ExtractSubset(cropped, rect)) return null;
            using var image = SKImage.FromBitmap(cropped);
            using var data = image.Encode(SKEncodedImageFormat.Jpeg, 92);
            return data.ToArray();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "크롭 실패 {Path}", sourcePath);
            return null;
        }
    }

    private static byte[]? Encode(SKBitmap source, int maxEdge, int quality)
    {
        int longest = Math.Max(source.Width, source.Height);
        var (w, h) = longest <= maxEdge
            ? (source.Width, source.Height)
            : ((int)Math.Round(source.Width * (double)maxEdge / longest),
               (int)Math.Round(source.Height * (double)maxEdge / longest));

        using var resized = source.Resize(new SKImageInfo(Math.Max(1, w), Math.Max(1, h)), SKFilterQuality.Medium);
        if (resized is null) return null;
        using var image = SKImage.FromBitmap(resized);
        using var data = image.Encode(SKEncodedImageFormat.Jpeg, quality);
        return data?.ToArray();
    }

    /// <summary>
    /// 흐림·노출을 잰다. 이미 디코딩해 둔 축소본(<see cref="ViewMaxEdge"/> 이하)에서 재고,
    /// 그 위에서 다시 줄이지 않는다 — 줄이면 흐림이 사라져 흐린 사진도 또렷하게 나온다.
    ///
    /// <para>
    /// 그래서 <see cref="ImageQualityMetrics.Sharpness"/> 는 <b>이 축소본 기준</b> 값이다.
    /// 원본 해상도가 서로 다른 사진끼리 이 값을 견주면 안 된다.
    /// </para>
    /// </summary>
    private static ImageQualityMetrics MeasureQuality(SKBitmap bitmap)
    {
        int w = bitmap.Width, h = bitmap.Height;
        if (w <= 0 || h <= 0) return default;

        var gray = new byte[w * h];
        for (int y = 0, i = 0; y < h; y++)
            for (int x = 0; x < w; x++, i++)
            {
                var c = bitmap.GetPixel(x, y);
                gray[i] = (byte)((c.Red * 299 + c.Green * 587 + c.Blue * 114) / 1000);
            }
        return ImageQuality.Measure(gray, w, h);
    }

    /// <summary>9×8 회색조로 줄여 dHash 를 만든다. 계산 자체는 Core 가 한다.</summary>
    private static ulong ComputeDHash(SKBitmap source)
    {
        using var small = source.Resize(
            new SKImageInfo(PerceptualHash.SampleWidth, PerceptualHash.SampleHeight, SKColorType.Rgba8888, SKAlphaType.Unpremul),
            SKFilterQuality.Medium);
        if (small is null) return 0;

        var gray = new byte[PerceptualHash.SampleWidth * PerceptualHash.SampleHeight];
        for (int y = 0, i = 0; y < PerceptualHash.SampleHeight; y++)
            for (int x = 0; x < PerceptualHash.SampleWidth; x++, i++)
            {
                var c = small.GetPixel(x, y);
                gray[i] = (byte)((c.Red * 299 + c.Green * 587 + c.Blue * 114) / 1000);
            }
        return PerceptualHash.FromGrayscale9x8(gray);
    }

    private static float ScaleFor(int width, int height, int maxEdge)
    {
        int longest = Math.Max(width, height);
        return longest <= maxEdge ? 1f : (float)maxEdge / longest;
    }

    /// <summary>확장자에서 content type 을 고른다. 저장은 원본 그대로 하므로 형식을 바꾸지 않는다.</summary>
    public static string ContentTypeFor(string fileName) => Path.GetExtension(fileName).ToLowerInvariant() switch
    {
        ".png" => "image/png",
        ".bmp" => "image/bmp",
        ".webp" => "image/webp",
        ".gif" => "image/gif",
        ".tif" or ".tiff" => "image/tiff",
        _ => "image/jpeg",
    };

    public static bool IsSupportedExtension(string fileName) =>
        Path.GetExtension(fileName).ToLowerInvariant() is ".jpg" or ".jpeg" or ".png" or ".bmp" or ".webp" or ".gif";
}
