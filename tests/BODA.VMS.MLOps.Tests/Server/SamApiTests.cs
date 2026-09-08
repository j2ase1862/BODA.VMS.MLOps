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
    /// <summary>있으면 후보를 여러 개 내주는 패치본을 쓴다 — 운영 기본값과 같은 길을 시험한다.</summary>
    public static string DecoderPath =>
        MultiMaskDecoderPresent
            ? Path.Combine(ModelDirectory, "mobile_sam_decoder_multi.onnx")
            : Path.Combine(ModelDirectory, "mobile_sam_decoder.onnx");
    public static bool ModelsPresent => File.Exists(EncoderPath) && File.Exists(DecoderPath);

    /// <summary>후보 여러 개를 내주는 패치본이 있는가 (scripts/patch_sam_decoder_multimask.py)</summary>
    public static bool MultiMaskDecoderPresent =>
        File.Exists(Path.Combine(ModelDirectory, "mobile_sam_decoder_multi.onnx"));

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

        body.Candidates.Should().NotBeEmpty(body.Message);
        body.Best.Should().BeInRange(0, body.Candidates.Count - 1);
        var mask = body.Candidates[body.Best];
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
            body.Candidates.Should().NotBeEmpty(body.Message);
            return body.Candidates[Math.Max(0, body.Best)];
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
        var body = (await res.Content.ReadFromJsonAsync<SamPredictResponse>(Json))!;
        var mask = body.Candidates[Math.Max(0, body.Best)];

        // 모델의 IoU 예측은 1 을 넘길 수 있다. 화면이 "101%" 를 보여 주지 않도록 서버가 잘라 준다.
        mask.Score.Should().BeInRange(0, 1);
    }

    [Fact]
    public async Task 후보를_여러_개_준다()
    {
        // 클릭 한 번의 뜻은 애매하다. "이것만" 과 "이것이 놓인 것" 을 사람이 고를 수 있어야 한다.
        if (!SamEnabledFactory.ModelsPresent) return;

        const int width = 640, height = 480;
        using var bitmap = new SKBitmap(width, height);
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(new SKColor(24, 26, 30));
            using var panel = new SKPaint { Color = new SKColor(140, 140, 140) };
            using var spot = new SKPaint { Color = new SKColor(235, 90, 60) };
            canvas.DrawRect(SKRect.Create(80, 60, 480, 360), panel);      // 큰 판
            canvas.DrawOval(SKRect.Create(280, 200, 90, 70), spot);       // 그 위의 작은 얼룩
        }
        using var encoded = SKImage.FromBitmap(bitmap);
        using var data = encoded.Encode(SKEncodedImageFormat.Png, 100);

        var eng = await _f.EngineerAsync();
        using var form = new MultipartFormDataContent();
        form.Add(new ByteArrayContent(data.ToArray()), "files", "nested.png");
        var upload = await eng.PostAsync("/api/images", form);
        upload.EnsureSuccessStatusCode();
        var image = (await upload.Content.ReadFromJsonAsync<ImageUploadBatchDto>(Json))!.Results[0].Image;

        // 패치하지 않은 디코더면 이 시험은 뜻이 없다. 상태 대신 파일로 판단한다 —
        // 상태의 MultiMask 는 모델을 올린 뒤에만 참이라 예열 시점을 타기 때문이다.
        if (!SamEnabledFactory.MultiMaskDecoderPresent) return;

        var res = await eng.PostAsJsonAsync("/api/sam/predict",
            new SamPredictRequest(image.Id, [new SamPointDto(325.0 / width, 235.0 / height)]), Json);
        res.EnsureSuccessStatusCode();
        var body = (await res.Content.ReadFromJsonAsync<SamPredictResponse>(Json))!;

        body.Candidates.Count.Should().BeGreaterThan(1, "얼룩만 원한 것인지 판까지 원한 것인지 모델은 모른다");
        // 작은 것부터 큰 것 순이어야 "더 크게 / 더 작게" 가 말이 된다
        body.Candidates.Select(c => c.PixelArea).Should().BeInAscendingOrder();
        // 가장 작은 후보는 얼룩, 가장 큰 후보는 판 정도는 돼야 한다
        body.Candidates[0].W.Should().BeLessThan(body.Candidates[^1].W);
    }

    [Fact]
    public async Task 배경_점을_찍으면_그_부분이_빠진다()
    {
        // 되먹임(mask_input)이 빠지면 두 번째 점이 잘 듣지 않는다.
        if (!SamEnabledFactory.ModelsPresent) return;

        const int width = 640, height = 480;
        using var bitmap = new SKBitmap(width, height);
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(new SKColor(24, 26, 30));
            using var paint = new SKPaint { Color = new SKColor(200, 200, 200) };
            // 가로로 붙어 있는 두 칸 — 왼쪽만 원한다고 알려 줄 수 있어야 한다
            canvas.DrawRect(SKRect.Create(120, 160, 200, 160), paint);
            canvas.DrawRect(SKRect.Create(320, 160, 200, 160), paint);
        }
        using var encoded = SKImage.FromBitmap(bitmap);
        using var data = encoded.Encode(SKEncodedImageFormat.Png, 100);

        var eng = await _f.EngineerAsync();
        using var form = new MultipartFormDataContent();
        form.Add(new ByteArrayContent(data.ToArray()), "files", "halves.png");
        var upload = await eng.PostAsync("/api/images", form);
        upload.EnsureSuccessStatusCode();
        var image = (await upload.Content.ReadFromJsonAsync<ImageUploadBatchDto>(Json))!.Results[0].Image;

        async Task<SamMaskDto> PredictAsync(SamPointDto[] points, int? prefer)
        {
            var res = await eng.PostAsJsonAsync("/api/sam/predict",
                new SamPredictRequest(image.Id, points, prefer), Json);
            res.EnsureSuccessStatusCode();
            var body = (await res.Content.ReadFromJsonAsync<SamPredictResponse>(Json))!;
            body.Candidates.Should().NotBeEmpty(body.Message);
            return body.Candidates[Math.Max(0, body.Best)];
        }

        // 먼저 왼쪽 칸을 집는다 (되먹임 캐시가 이 단계의 마스크를 기억한다)
        SamPointDto left = new(220.0 / width, 240.0 / height);
        var first = await PredictAsync([left], null);

        // 이어서 오른쪽 칸을 "빼라" 고 알린다
        var exclude = new SamPointDto(420.0 / width, 240.0 / height, Foreground: false);
        var second = await PredictAsync([left, exclude], null);

        // 계약은 하나다 — 빼라고 한 자리가 결과 안에 있으면 안 된다.
        // (두 칸이 같은 색으로 맞붙어 있어 경계가 없으므로 정확히 절반에서 잘리지는 않는다.)
        bool inside = exclude.X >= second.X && exclude.X <= second.X + second.W
                   && exclude.Y >= second.Y && exclude.Y <= second.Y + second.H;
        inside.Should().BeFalse(
            $"빼라고 한 점이 여전히 들어 있다 (처음 {first.X + first.W:F2} → 이후 {second.X + second.W:F2})");

        // 그리고 눈에 띄게 줄어야 한다. 되먹임이 빠지면 거의 그대로 나온다.
        second.PixelArea.Should().BeLessThan((int)(first.PixelArea * 0.9));
        second.X.Should().BeApproximately(120.0 / width, 0.06);
    }

    [Fact]
    public async Task 가려져_두_조각이_되면_클릭한_조각을_돌려준다()
    {
        // 예전에는 가장 큰 덩어리만 남겨서, 사용자가 집은 작은 조각이 버려졌다.
        if (!SamEnabledFactory.ModelsPresent) return;

        const int width = 640, height = 480;
        using var bitmap = new SKBitmap(width, height);
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(new SKColor(24, 26, 30));
            using var bar = new SKPaint { Color = new SKColor(210, 210, 210) };
            using var cover = new SKPaint { Color = new SKColor(24, 26, 30) };
            canvas.DrawRect(SKRect.Create(60, 220, 520, 40), bar);      // 긴 막대
            canvas.DrawRect(SKRect.Create(300, 200, 60, 80), cover);    // 가운데를 가린다
        }
        using var encoded = SKImage.FromBitmap(bitmap);
        using var data = encoded.Encode(SKEncodedImageFormat.Png, 100);

        var eng = await _f.EngineerAsync();
        using var form = new MultipartFormDataContent();
        form.Add(new ByteArrayContent(data.ToArray()), "files", "occluded.png");
        var upload = await eng.PostAsync("/api/images", form);
        upload.EnsureSuccessStatusCode();
        var image = (await upload.Content.ReadFromJsonAsync<ImageUploadBatchDto>(Json))!.Results[0].Image;

        // 짧은 오른쪽 조각(360..580)을 집는다 — 왼쪽(60..300)이 더 길다
        double clickX = 470.0 / width, clickY = 240.0 / height;
        var res = await eng.PostAsJsonAsync("/api/sam/predict",
            new SamPredictRequest(image.Id, [new SamPointDto(clickX, clickY)]), Json);
        res.EnsureSuccessStatusCode();
        var body = (await res.Content.ReadFromJsonAsync<SamPredictResponse>(Json))!;
        body.Candidates.Should().NotBeEmpty(body.Message);

        // 어떤 후보를 고르든, 돌려준 도형은 클릭한 자리를 담고 있어야 한다
        foreach (var candidate in body.Candidates)
        {
            clickX.Should().BeInRange(candidate.X, candidate.X + candidate.W,
                "클릭한 점이 결과 밖에 있으면 사용자는 이유를 알 수 없다");
            clickY.Should().BeInRange(candidate.Y, candidate.Y + candidate.H);
        }
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
