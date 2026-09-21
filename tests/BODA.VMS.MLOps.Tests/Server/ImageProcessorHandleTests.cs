using BODA.VMS.MLOps.Server.Services;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using SkiaSharp;

namespace BODA.VMS.MLOps.Tests.Server;

/// <summary>
/// 사진을 읽고 나면 그 파일을 놓아 주는지.
///
/// <para>
/// 업로드는 임시 파일에 받아 두고, 읽어서 썸네일을 만든 다음 그 파일을 <b>제자리로 옮깁니다</b>.
/// 읽던 손잡이가 남아 있으면 옮기기가 "다른 프로세스가 사용 중" 으로 터지고 업로드가 500 이 됩니다.
/// GC 가 늦게 돌 때만 나므로 <b>가끔</b> 실패하고, 그래서 오래 남습니다 —
/// 이 리포의 시험 묶음이 다섯 번에 한 번씩 이 이유로 흔들렸습니다.
/// </para>
/// </summary>
public class ImageProcessorHandleTests
{
    private static byte[] Png(int width, int height, int seed)
    {
        using var bitmap = new SKBitmap(width, height);
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(new SKColor((byte)(seed * 31 % 255), (byte)(seed * 57 % 255), 90));
            using var paint = new SKPaint { Color = SKColors.White };
            canvas.DrawRect(SKRect.Create(seed % 40, 10, 60, 60), paint);
        }
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    /// <summary>
    /// 읽은 직후 옮길 수 있어야 한다. 한 번은 우연히 통과할 수 있으므로 여러 번 본다 —
    /// 손잡이가 남는 문제는 GC 시점에 달려 있어 드문드문 나타난다.
    /// </summary>
    [Fact]
    public void The_file_can_be_moved_right_after_it_is_read()
    {
        var processor = new ImageProcessor(NullLogger<ImageProcessor>.Instance);
        var dir = Path.Combine(Path.GetTempPath(), "mlops-handle-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);

        try
        {
            for (var i = 0; i < 60; i++)
            {
                var source = Path.Combine(dir, $"in-{i}.png");
                var target = Path.Combine(dir, $"out-{i}.png");
                File.WriteAllBytes(source, Png(400, 300, i));

                var processed = processor.Process(source);
                processed.Width.Should().Be(400);

            var move = () => File.Move(source, target, overwrite: false);
                move.Should().NotThrow<IOException>($"{i}번째 사진을 읽은 뒤에도 파일이 잡혀 있으면 안 된다");
            }
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    /// <summary>
    /// 받는 확장자는 <b>화면의 파일 고르개와 같아야 한다</b>.
    /// 화면(<c>Shared/ImageUploadDialog.razor</c> 의 <c>Accept</c>)이 여기보다 넓으면 사람이 고를 수 있는 파일이
    /// 올리는 순간 거부되고, 좁으면 받을 수 있는 파일을 고르지 못한다. 어느 쪽이든 화면에서만 드러난다.
    /// </summary>
    [Fact]
    public void Supported_extensions_are_exactly_what_the_upload_dialog_offers()
    {
        string[] offered = [".jpg", ".jpeg", ".png", ".bmp", ".webp", ".gif"];
        foreach (var ext in offered)
            ImageProcessor.IsSupportedExtension("사진" + ext).Should().BeTrue(ext);

        // 화면이 내밀지 않는 것은 서버도 받지 않는다 — tif 는 내용 유형 표에만 있고 허용 목록에는 없다
        foreach (var ext in new[] { ".tif", ".tiff", ".heic", ".svg", ".exe", "" })
            ImageProcessor.IsSupportedExtension("사진" + ext).Should().BeFalse(ext);
    }
}
