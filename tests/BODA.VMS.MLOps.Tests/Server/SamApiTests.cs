using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using BODA.VMS.MLOps.Contracts.Datasets;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using SkiaSharp;
using static BODA.VMS.MLOps.Tests.Server.MlopsApiFactory;

namespace BODA.VMS.MLOps.Tests.Server;

/// <summary>
/// SAM 보조 라벨링 API (개발 문서 §5.4).
/// 모델을 두지 않은 환경이 기본이므로, 그때 서버가 조용히 "없음"으로 답하는지를 먼저 지킨다.
/// 모델이 있으면 실제 추론까지 확인한다 (<see cref="SamInferenceTests"/>).
/// </summary>
public class SamApiTests : IClassFixture<MlopsApiFactory>
{
    private readonly MlopsApiFactory _f;
    public SamApiTests(MlopsApiFactory f) => _f = f;

    private async Task<ImageDto> UploadAsync(HttpClient client)
    {
        using var form = new MultipartFormDataContent();
        form.Add(new ByteArrayContent(DataManagementApiTests.MakePng(64, 48, SKColors.DarkSlateGray, 3)), "files", "sam.png");
        var res = await client.PostAsync("/api/images", form);
        res.EnsureSuccessStatusCode();
        return (await res.Content.ReadFromJsonAsync<ImageUploadBatchDto>(Json))!.Results[0].Image;
    }

    [Fact]
    public async Task 모델이_없으면_사용_불가로_답한다()
    {
        var eng = await _f.EngineerAsync();

        var status = await eng.GetFromJsonAsync<SamStatusDto>("/api/sam/status", Json);

        status!.Available.Should().BeFalse();
        status.Ready.Should().BeFalse();
        status.Message.Should().NotBeNullOrWhiteSpace("화면이 왜 못 쓰는지 사람에게 알려 줘야 한다");
    }

    [Fact]
    public async Task 모델이_없을_때_예측은_오류가_아니라_400_설명이다()
    {
        var eng = await _f.EngineerAsync();
        var image = await UploadAsync(eng);

        var res = await eng.PostAsJsonAsync("/api/sam/predict",
            new SamPredictRequest(image.Id, [new SamPointDto(0.5, 0.5)]), Json);

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ErrorAsync(res))!.Message.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task 모델이_없어도_준비_요청은_조용히_넘어간다()
    {
        // 라벨링 화면이 사진을 열 때마다 부른다. 모델이 없다고 오류를 띄우면 화면이 시끄러워진다.
        var eng = await _f.EngineerAsync();
        var image = await UploadAsync(eng);

        var res = await eng.PostAsJsonAsync("/api/sam/prepare", new SamPrepareRequest(image.Id), Json);

        res.StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task 익명_요청은_막는다()
    {
        var anon = _f.CreateClient();

        (await anon.GetAsync("/api/sam/status")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await anon.PostAsJsonAsync("/api/sam/predict",
            new SamPredictRequest(Guid.NewGuid(), [new SamPointDto(0.5, 0.5)]), Json))
            .StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task 조회_권한만_있으면_상태는_보되_예측은_못_한다()
    {
        // 상태는 화면 구성에 필요하고, 예측은 라벨을 만드는 행위라 라벨러 이상이어야 한다
        var viewer = await _f.ViewerAsync();

        (await viewer.GetAsync("/api/sam/status")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await viewer.PostAsJsonAsync("/api/sam/predict",
            new SamPredictRequest(Guid.NewGuid(), [new SamPointDto(0.5, 0.5)]), Json))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await viewer.PostAsJsonAsync("/api/sam/prepare", new SamPrepareRequest(Guid.NewGuid()), Json))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }
}

/// <summary>MobileSAM 모델을 실제로 올려 두는 팩토리. 파일이 없으면 시험이 스스로 비켜난다.</summary>
public class SamEnabledFactory : MlopsApiFactory
{
    public static string ModelDirectory { get; } = FindModelDirectory();
    public static string EncoderPath => Path.Combine(ModelDirectory, "mobile_sam_encoder.onnx");
    public static string DecoderPath => Path.Combine(ModelDirectory, "mobile_sam_decoder.onnx");
    public static bool ModelsPresent => File.Exists(EncoderPath) && File.Exists(DecoderPath);

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.UseSetting("Sam:EncoderPath", EncoderPath);
        builder.UseSetting("Sam:DecoderPath", DecoderPath);
    }

    /// <summary>솔루션 파일을 찾아 올라가 서버 프로젝트의 models/sam 을 가리킨다.</summary>
    private static string FindModelDirectory()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !directory.EnumerateFiles("*.slnx").Any())
            directory = directory.Parent;
        return directory is null
            ? "models/sam"
            : Path.Combine(directory.FullName, "src", "BODA.VMS.MLOps.Server", "models", "sam");
    }
}

/// <summary>
/// 실제 MobileSAM 추론. 모델 파일(약 88MB)은 저장소에 없으므로 없는 환경에서는 그냥 지나간다.
/// 있는 환경에서는 좌표 규약이 어긋나면 여기서 잡힌다 — 마스크가 뒤집히거나 축척이 틀리면
/// 폴리곤이 엉뚱한 곳에 생기는데, 화면으로만 보면 "그럴듯하게" 보여 놓치기 쉽다.
/// </summary>
public class SamInferenceTests : IClassFixture<SamEnabledFactory>
{
    private readonly SamEnabledFactory _f;
    public SamInferenceTests(SamEnabledFactory f) => _f = f;

    /// <summary>어두운 배경 가운데 밝은 사각형 하나. 클릭하면 그 사각형이 나와야 한다.</summary>
    private static byte[] MakeTargetPng(int width, int height, SKRect target)
    {
        using var bitmap = new SKBitmap(width, height);
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(new SKColor(30, 32, 38));
            using var paint = new SKPaint { Color = new SKColor(235, 90, 60), IsAntialias = false };
            canvas.DrawRect(target, paint);
        }
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    [Fact]
    public async Task 클릭한_객체의_폴리곤이_그_자리에_생긴다()
    {
        if (!SamEnabledFactory.ModelsPresent) return;

        const int width = 512, height = 384;
        var target = SKRect.Create(150, 100, 200, 180);   // 오른쪽·아래로 150..350, 100..280

        var eng = await _f.EngineerAsync();
        using var form = new MultipartFormDataContent();
        form.Add(new ByteArrayContent(MakeTargetPng(width, height, target)), "files", "target.png");
        var upload = await eng.PostAsync("/api/images", form);
        upload.EnsureSuccessStatusCode();
        var image = (await upload.Content.ReadFromJsonAsync<ImageUploadBatchDto>(Json))!.Results[0].Image;

        var status = await eng.GetFromJsonAsync<SamStatusDto>("/api/sam/status", Json);
        status!.Available.Should().BeTrue();

        // 사각형 한가운데를 한 번 클릭한다
        var res = await eng.PostAsJsonAsync("/api/sam/predict",
            new SamPredictRequest(image.Id, [new SamPointDto(250.0 / width, 190.0 / height)]), Json);
        res.EnsureSuccessStatusCode();
        var body = (await res.Content.ReadFromJsonAsync<SamPredictResponse>(Json))!;

        body.Mask.Should().NotBeNull(body.Message);
        var mask = body.Mask!;
        mask.Points.Length.Should().BeGreaterThanOrEqualTo(3);
        mask.Points.Should().OnlyContain(p => p[0] >= 0 && p[0] <= 1 && p[1] >= 0 && p[1] <= 1);

        // 외접 박스가 실제 사각형과 맞아야 한다. 축척·전치가 틀리면 여기서 크게 벌어진다.
        mask.X.Should().BeApproximately(150.0 / width, 0.05);
        mask.Y.Should().BeApproximately(100.0 / height, 0.05);
        mask.W.Should().BeApproximately(200.0 / width, 0.08);
        mask.H.Should().BeApproximately(180.0 / height, 0.08);
        mask.Score.Should().BeGreaterThan(0.5);
    }

    [Fact]
    public async Task 같은_사진을_다시_물으면_임베딩을_다시_만들지_않는다()
    {
        if (!SamEnabledFactory.ModelsPresent) return;

        const int width = 512, height = 384;
        var eng = await _f.EngineerAsync();
        using var form = new MultipartFormDataContent();
        form.Add(new ByteArrayContent(MakeTargetPng(width, height, SKRect.Create(200, 120, 160, 160))), "files", "cache.png");
        var upload = await eng.PostAsync("/api/images", form);
        upload.EnsureSuccessStatusCode();
        var image = (await upload.Content.ReadFromJsonAsync<ImageUploadBatchDto>(Json))!.Results[0].Image;

        var request = new SamPredictRequest(image.Id, [new SamPointDto(0.55, 0.52)]);

        var first = System.Diagnostics.Stopwatch.StartNew();
        (await eng.PostAsJsonAsync("/api/sam/predict", request, Json)).EnsureSuccessStatusCode();
        first.Stop();

        var second = System.Diagnostics.Stopwatch.StartNew();
        (await eng.PostAsJsonAsync("/api/sam/predict", request, Json)).EnsureSuccessStatusCode();
        second.Stop();

        // 두 번째는 인코더를 건너뛰므로 확실히 빨라야 한다. 캐시가 빠지면 클릭마다 수백 ms 가 붙는다.
        second.ElapsedMilliseconds.Should().BeLessThan(Math.Max(80, first.ElapsedMilliseconds / 2));
    }

    [Fact]
    public async Task 서로_다른_객체를_클릭하면_서로_다른_폴리곤이_나온다()
    {
        // 좌표를 통째로 무시해도 "그럴듯한" 마스크 하나는 나온다. 두 객체를 갈라 보면 그 함정이 드러난다.
        if (!SamEnabledFactory.ModelsPresent) return;

        const int width = 640, height = 480;
        var left = SKRect.Create(60, 150, 140, 180);      // 60..200
        var right = SKRect.Create(400, 120, 160, 200);    // 400..560

        using var bitmap = new SKBitmap(width, height);
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(new SKColor(28, 30, 34));
            using var one = new SKPaint { Color = new SKColor(235, 90, 60) };
            using var two = new SKPaint { Color = new SKColor(70, 160, 235) };
            canvas.DrawRect(left, one);
            canvas.DrawRect(right, two);
        }
        using var encoded = SKImage.FromBitmap(bitmap);
        using var data = encoded.Encode(SKEncodedImageFormat.Png, 100);

        var eng = await _f.EngineerAsync();
        using var form = new MultipartFormDataContent();
        form.Add(new ByteArrayContent(data.ToArray()), "files", "two.png");
        var upload = await eng.PostAsync("/api/images", form);
        upload.EnsureSuccessStatusCode();
        var image = (await upload.Content.ReadFromJsonAsync<ImageUploadBatchDto>(Json))!.Results[0].Image;

        async Task<SamMaskDto> ClickAsync(double x, double y)
        {
            var res = await eng.PostAsJsonAsync("/api/sam/predict",
                new SamPredictRequest(image.Id, [new SamPointDto(x / width, y / height)]), Json);
            res.EnsureSuccessStatusCode();
            var body = (await res.Content.ReadFromJsonAsync<SamPredictResponse>(Json))!;
            body.Mask.Should().NotBeNull(body.Message);
            return body.Mask!;
        }

        var leftMask = await ClickAsync(130, 240);
        var rightMask = await ClickAsync(480, 220);

        // 각자 자기 사각형만 잡아야 한다
        leftMask.X.Should().BeApproximately(60.0 / width, 0.05);
        (leftMask.X + leftMask.W).Should().BeApproximately(200.0 / width, 0.05);
        rightMask.X.Should().BeApproximately(400.0 / width, 0.05);
        (rightMask.X + rightMask.W).Should().BeApproximately(560.0 / width, 0.05);

        // 두 마스크가 겹치지 않아야 한다 — 겹치면 좌표가 무시되고 있다는 뜻이다
        (leftMask.X + leftMask.W).Should().BeLessThan(rightMask.X);
    }

    [Fact]
    public async Task 점수는_0과_1_사이로_돌아온다()
    {
        if (!SamEnabledFactory.ModelsPresent) return;

        const int width = 512, height = 384;
        var eng = await _f.EngineerAsync();
        using var form = new MultipartFormDataContent();
        form.Add(new ByteArrayContent(MakeTargetPng(width, height, SKRect.Create(180, 120, 150, 150))), "files", "score.png");
        var upload = await eng.PostAsync("/api/images", form);
        upload.EnsureSuccessStatusCode();
        var image = (await upload.Content.ReadFromJsonAsync<ImageUploadBatchDto>(Json))!.Results[0].Image;

        var res = await eng.PostAsJsonAsync("/api/sam/predict",
            new SamPredictRequest(image.Id, [new SamPointDto(0.5, 0.5)]), Json);
        res.EnsureSuccessStatusCode();
        var mask = (await res.Content.ReadFromJsonAsync<SamPredictResponse>(Json))!.Mask!;

        // 모델의 IoU 예측은 1 을 넘길 수 있다. 화면이 "101%" 를 보여 주지 않도록 서버가 잘라 준다.
        mask.Score.Should().BeInRange(0, 1);
    }

    [Fact]
    public async Task 전경_점이_하나도_없으면_거절한다()
    {
        if (!SamEnabledFactory.ModelsPresent) return;

        var eng = await _f.EngineerAsync();
        var res = await eng.PostAsJsonAsync("/api/sam/predict",
            new SamPredictRequest(Guid.NewGuid(), [new SamPointDto(0.5, 0.5, Foreground: false)]), Json);

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }
}
