using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using BODA.VMS.MLOps.Contracts.Datasets;
using FluentAssertions;
using SkiaSharp;
using static BODA.VMS.MLOps.Tests.Server.MlopsApiFactory;

namespace BODA.VMS.MLOps.Tests.Server;

/// <summary>
/// 업로드한 이미지의 흐림·노출 지표가 실제로 재어져 저장되고, 그것으로 걸러 볼 수 있는지.
///
/// <para>
/// 이 기능은 사람이 "어떤 사진을 지울까" 를 판단하는 근거가 된다. 지표가 뒤집혀 있으면
/// 멀쩡한 사진이 지워지고 흐린 사진이 남는다. 그래서 값 자체가 아니라 <b>순서</b>를 못 박는다.
/// </para>
/// </summary>
public class ImageQualityApiTests : IClassFixture<MlopsApiFactory>
{
    private readonly MlopsApiFactory _f;
    public ImageQualityApiTests(MlopsApiFactory f) => _f = f;

    // 업로드는 내용 해시로 중복을 제거한다. 시험마다 바이트가 같으면 두 번째부터 created=false 가
    // 되고 파일 이름도 처음 것으로 남아, 이름으로 찾는 시험이 빈손이 된다.
    // 그래서 모든 그림에 seed 로 만든 자국을 남긴다 — 4×4 = 16 화소라 지표에는 영향이 없다
    // (256×256 의 0.02%).
    private const int Size = 256;
    private const int MarkEdge = 4;

    /// <summary>또렷한 무늬 — 촘촘한 격자를 그린다.</summary>
    private static byte[] SharpPng(int seed)
    {
        using var bitmap = new SKBitmap(Size, Size);
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(SKColors.White);
            using var paint = new SKPaint { Color = SKColors.Black };
            for (int y = 0; y < Size; y += 8)
                for (int x = 0; x < Size; x += 8)
                    if ((x / 8 + y / 8) % 2 == 0)
                        canvas.DrawRect(SKRect.Create(x, y, 8, 8), paint);
        }
        Mark(bitmap, seed, 0, 255);
        return Encode(bitmap);
    }

    /// <summary>같은 무늬를 흐리게 — 초점이 나간 사진에 해당한다.</summary>
    private static byte[] BlurryPng(int seed)
    {
        using var source = SKBitmap.Decode(SharpPng(seed));
        using var surface = SKSurface.Create(new SKImageInfo(Size, Size));
        using (var paint = new SKPaint { ImageFilter = SKImageFilter.CreateBlur(6, 6) })
            surface.Canvas.DrawBitmap(source, 0, 0, paint);
        using var snapshot = surface.Snapshot();
        using var data = snapshot.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    /// <summary>노출 부족 — 거의 검다. 자국도 검은 범위 안에서 흔들어 클리핑 비율을 지킨다.</summary>
    private static byte[] DarkPng(int seed) => Solid(new SKColor(4, 4, 4), seed, 0, 12);

    /// <summary>노출 과다 — 거의 희다. 자국은 흰 범위 안에서.</summary>
    private static byte[] BrightPng(int seed) => Solid(new SKColor(252, 252, 252), seed, 247, 255);

    private static byte[] Solid(SKColor color, int seed, byte markLow, byte markHigh)
    {
        using var bitmap = new SKBitmap(Size, Size);
        using (var canvas = new SKCanvas(bitmap)) canvas.Clear(color);
        Mark(bitmap, seed, markLow, markHigh);
        return Encode(bitmap);
    }

    /// <summary>
    /// 왼쪽 위 모서리에 seed 로 만든 자국을 남긴다. 값은 [low, high] 안에서만 움직여
    /// 그 그림이 뜻하는 노출(어둡다/밝다)을 깨지 않는다.
    /// </summary>
    private static void Mark(SKBitmap bitmap, int seed, byte low, byte high)
    {
        var random = new Random(seed);
        int span = Math.Max(1, high - low + 1);
        for (int y = 0; y < MarkEdge; y++)
            for (int x = 0; x < MarkEdge; x++)
            {
                byte v = (byte)(low + random.Next(span));
                bitmap.SetPixel(x, y, new SKColor(v, v, v));
            }
    }

    private static byte[] Encode(SKBitmap bitmap)
    {
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    /// <summary>이름으로 찾을 꼬리표와, 그림을 서로 다르게 만들 씨앗을 함께 만든다.</summary>
    private static (string Tag, int Seed) NewTag()
    {
        var id = Guid.NewGuid();
        return (id.ToString("N")[..8], id.GetHashCode());
    }

    private static async Task<ImageUploadBatchDto> UploadAsync(HttpClient client, params (string Name, byte[] Bytes)[] files)
    {
        using var form = new MultipartFormDataContent();
        foreach (var (name, bytes) in files)
        {
            var part = new ByteArrayContent(bytes);
            part.Headers.ContentType = new MediaTypeHeaderValue("image/png");
            form.Add(part, "files", name);
        }
        form.Add(new StringContent("manual"), "source");
        var res = await client.PostAsync("/api/images", form);
        res.StatusCode.Should().Be(HttpStatusCode.OK, await res.Content.ReadAsStringAsync());
        return (await res.Content.ReadFromJsonAsync<ImageUploadBatchDto>(Json))!;
    }

    [Fact]
    public async Task Upload_measures_blur_and_exposure()
    {
        var eng = await _f.EngineerAsync();
        var (tag, seed) = NewTag();

        var upload = await UploadAsync(eng,
            ($"sharp-{tag}.png", SharpPng(seed)),
            ($"blurry-{tag}.png", BlurryPng(seed + 1)),
            ($"dark-{tag}.png", DarkPng(seed + 2)),
            ($"bright-{tag}.png", BrightPng(seed + 3)));
        upload.Created.Should().Be(4);

        var byName = upload.Results.ToDictionary(r => r.Image.FileName, r => r.Image);
        var sharp = byName[$"sharp-{tag}.png"].Quality;
        var blurry = byName[$"blurry-{tag}.png"].Quality;
        var dark = byName[$"dark-{tag}.png"].Quality;
        var bright = byName[$"bright-{tag}.png"].Quality;

        sharp.Should().NotBeNull("업로드하면 지표가 재어져야 한다");
        blurry.Should().NotBeNull();

        // 흐림: 뒤집혀 있으면 사람이 또렷한 사진부터 지우게 된다
        blurry!.Sharpness.Should().BeLessThan(sharp!.Sharpness);

        // 노출: 어느 쪽으로 날아갔는지 나눠 세야 원인을 안다
        dark!.ClippedDarkRatio.Should().BeGreaterThan(0.9);
        dark.ClippedBrightRatio.Should().BeLessThan(0.1);
        bright!.ClippedBrightRatio.Should().BeGreaterThan(0.9);
        bright.ClippedDarkRatio.Should().BeLessThan(0.1);

        dark.MeanLuma.Should().BeLessThan(bright.MeanLuma);
    }

    [Fact]
    public async Task Blurriest_first_puts_the_blurry_one_ahead()
    {
        var eng = await _f.EngineerAsync();
        var (tag, seed) = NewTag();

        await UploadAsync(eng, ($"a-sharp-{tag}.png", SharpPng(seed)), ($"a-blurry-{tag}.png", BlurryPng(seed + 1)));

        var page = await eng.GetFromJsonAsync<ImagePageDto>(
            $"/api/images?search={tag}&blurriestFirst=true&take=50", Json);

        page!.Items.Should().HaveCountGreaterThanOrEqualTo(2);
        page.Items[0].FileName.Should().Contain("blurry", "흐린 것부터 보여 줘야 사람이 확인할 후보가 앞에 온다");
    }

    /// <summary>흐림 문턱으로 좁히면 또렷한 사진은 빠져야 한다.</summary>
    [Fact]
    public async Task Max_sharpness_narrows_to_the_blurry_ones()
    {
        var eng = await _f.EngineerAsync();
        var (tag, seed) = NewTag();

        var upload = await UploadAsync(eng, ($"b-sharp-{tag}.png", SharpPng(seed)), ($"b-blurry-{tag}.png", BlurryPng(seed + 1)));
        var blurrySharpness = upload.Results.Single(r => r.Image.FileName.Contains("blurry")).Image.Quality!.Sharpness;
        var sharpSharpness = upload.Results.Single(r => r.Image.FileName.Contains("b-sharp")).Image.Quality!.Sharpness;

        // 두 값 사이에 문턱을 둔다 — 절대 기준이 없으므로 시험도 상대적으로 잡는다
        var threshold = (blurrySharpness + sharpSharpness) / 2;

        var page = await eng.GetFromJsonAsync<ImagePageDto>(
            $"/api/images?search={tag}&maxSharpness={threshold.ToString(System.Globalization.CultureInfo.InvariantCulture)}&take=50", Json);

        page!.Items.Should().ContainSingle();
        page.Items[0].FileName.Should().Contain("blurry");
    }

    /// <summary>날아간 화소로 좁히면 노출이 정상인 사진은 빠져야 한다.</summary>
    [Fact]
    public async Task Min_clipped_ratio_narrows_to_the_burnt_ones()
    {
        var eng = await _f.EngineerAsync();
        var (tag, seed) = NewTag();

        await UploadAsync(eng,
            ($"c-mid-{tag}.png", Solid(new SKColor(128, 128, 128), seed, 120, 136)),
            ($"c-dark-{tag}.png", DarkPng(seed + 1)));

        var page = await eng.GetFromJsonAsync<ImagePageDto>(
            $"/api/images?search={tag}&minClippedRatio=0.5&take=50", Json);

        page!.Items.Should().ContainSingle();
        page.Items[0].FileName.Should().Contain("dark");
    }

    [Fact]
    public async Task Clipped_ratio_outside_zero_to_one_is_rejected()
    {
        var eng = await _f.EngineerAsync();

        var res = await eng.GetAsync("/api/images?minClippedRatio=1.5");

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }
}
