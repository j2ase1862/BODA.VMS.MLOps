using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using BODA.VMS.MLOps.Contracts;
using BODA.VMS.MLOps.Contracts.Datasets;
using BODA.VMS.MLOps.Contracts.Models;
using BODA.VMS.MLOps.Core.Domain;
using BODA.VMS.MLOps.Server.Data;
using BODA.VMS.MLOps.Server.Data.Entities;
using BODA.VMS.MLOps.Server.Services;
using Microsoft.Extensions.DependencyInjection;
using BODA.VMS.MLOps.Tests.TestAssets;
using FluentAssertions;
using SkiaSharp;
using static BODA.VMS.MLOps.Tests.Server.MlopsApiFactory;

namespace BODA.VMS.MLOps.Tests.Server;

/// <summary>
/// 모델 돌려 보기 (개발 문서 §5.5 3번) — 승격하는 사람이 사진 위의 결과를 보고 결정하게 한다.
/// 스텁 ONNX 는 상수 출력 그래프라 실제로 돈다. 그래서 경계와 응답 모양은 모델 파일 없이 확인하고,
/// 좌표가 맞는지는 진짜 모델이 있을 때(<see cref="ModelTryInferenceTests"/>) 본다.
/// </summary>
public class ModelTryTests : IClassFixture<MlopsApiFactory>
{
    private readonly MlopsApiFactory _f;
    public ModelTryTests(MlopsApiFactory f) => _f = f;

    private async Task<(HttpClient Eng, ModelVersionDto Version)> VersionAsync(string stub, TaskType task = TaskType.Detection)
    {
        var eng = await _f.EngineerAsync();
        var model = await CreateModelAsync(eng, $"try-{Guid.NewGuid():N}"[..12], task, ["good", "defect"]);
        var upload = await UploadVersionAsync(eng, model.Id, OnnxStubs.Bytes(stub),
            new VersionUploadMeta(Classes: ["good", "defect"], License: "AGPL-3.0 확인함"));
        upload.StatusCode.Should().Be(HttpStatusCode.Created, await upload.Content.ReadAsStringAsync());
        return (eng, (await upload.Content.ReadFromJsonAsync<ModelVersionDto>(Json))!);
    }

    internal static async Task<Guid[]> UploadImagesAsync(HttpClient client, int count, int seed)
    {
        using var form = new MultipartFormDataContent();
        for (int i = 0; i < count; i++)
        {
            var part = new ByteArrayContent(DataManagementApiTests.MakePng(160, 120, SKColors.DarkSlateGray, seed + i));
            part.Headers.ContentType = new MediaTypeHeaderValue("image/png");
            form.Add(part, "files", $"try-{Guid.NewGuid():N}.png");
        }
        var uploaded = (await (await client.PostAsync("/api/images", form)).Content.ReadFromJsonAsync<ImageUploadBatchDto>(Json))!;
        return uploaded.Results.Select(r => r.Image.Id).ToArray();
    }

    [Theory]
    [InlineData(OnnxStubs.YoloStub, ModelFormat.Yolo)]
    [InlineData(OnnxStubs.DeployWithMeta, ModelFormat.DFine)]
    public async Task Runs_on_the_chosen_images_in_order(string stub, ModelFormat format)
    {
        var (eng, version) = await VersionAsync(stub);
        version.Format.Should().Be(format);
        var ids = await UploadImagesAsync(eng, 3, seed: 700 + (int)format * 10);
        var missing = Guid.NewGuid();

        var res = await eng.PostAsJsonAsync($"/api/model-versions/{version.Id}/try", new TryModelRequest([ids[2], missing, ids[0], ids[1]]), Json);
        res.StatusCode.Should().Be(HttpStatusCode.OK, await res.Content.ReadAsStringAsync());
        var result = (await res.Content.ReadFromJsonAsync<TryModelResultDto>(Json))!;

        // 고른 순서 그대로 — 화면의 격자 순서와 같아야 겹쳐 그릴 때 사진이 엇갈리지 않는다
        result.Images.Select(i => i.ImageId).Should().Equal(ids[2], missing, ids[0], ids[1]);
        result.Images[1].Error.Should().NotBeNull("없는 사진은 그 자리에 이유를 남기고 나머지는 계속 돈다");
        result.Images.Where(i => i.ImageId != missing).Should().OnlyContain(i => i.Error == null);
        result.Classes.Should().Equal("good", "defect");
        result.ScoreFloor.Should().Be(ModelTryService.ScoreFloor);
        result.NmsIou.Should().Be(format == ModelFormat.Yolo ? ModelTryService.NmsIou : null);

        foreach (var d in result.Images.SelectMany(i => i.Detections))
        {
            d.Score.Should().BeGreaterThanOrEqualTo(ModelTryService.ScoreFloor);
            d.X.Should().BeInRange(0, 1);
            d.Y.Should().BeInRange(0, 1);
        }
    }

    [Fact]
    public async Task Refuses_what_it_cannot_run()
    {
        var (eng, version) = await VersionAsync(OnnxStubs.YoloStub);
        var ids = await UploadImagesAsync(eng, 1, seed: 760);

        (await eng.PostAsJsonAsync($"/api/model-versions/{Guid.NewGuid()}/try", new TryModelRequest(ids), Json))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);

        var empty = await eng.PostAsJsonAsync($"/api/model-versions/{version.Id}/try", new TryModelRequest([]), Json);
        empty.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var many = Enumerable.Range(0, ModelTryService.MaxImages + 1).Select(_ => Guid.NewGuid()).ToArray();
        var tooMany = await eng.PostAsJsonAsync($"/api/model-versions/{version.Id}/try", new TryModelRequest(many), Json);
        tooMany.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ErrorAsync(tooMany))!.Message.Should().Contain($"{ModelTryService.MaxImages}장");

        // 서버 CPU 로 추론하므로 승격을 결정하는 Engineer 이상만
        var labeler = await _f.ClientAsAsync("labeler-try", "Labeler");
        (await labeler.PostAsJsonAsync($"/api/model-versions/{version.Id}/try", new TryModelRequest(ids), Json))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Only_detection_models_for_now()
    {
        // 분류·이상탐지·OCR 은 확인한 후처리가 없다 — 그럴듯한 결과를 보여 주느니 거절한다
        var (eng, version) = await VersionAsync(OnnxStubs.YoloStub, TaskType.Detection);
        var ids = await UploadImagesAsync(eng, 1, seed: 770);
        (await eng.PostAsJsonAsync($"/api/model-versions/{version.Id}/try", new TryModelRequest(ids), Json))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        // 분류 모델 버전은 업로드 스텁이 없어 DB 에 바로 넣는다. 거절은 모델 파일을 열기 전에 일어나야 한다.
        var classifier = await CreateModelAsync(eng, $"try-cls-{Guid.NewGuid():N}"[..14], TaskType.Classification, ["good", "defect"]);
        var versionId = Guid.NewGuid();
        using (var scope = _f.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MlopsDbContext>();
            db.ModelVersions.Add(new ModelVersion
            {
                Id = versionId, ModelId = classifier.Id, Number = 1, Sha256 = new string('c', 64), ArtifactKey = "models/none.onnx",
                Format = ModelFormat.Classifier, ClassesJson = """["good","defect"]""", CreatedBy = "test", CreatedAt = DateTime.UtcNow,
            });
            await db.SaveChangesAsync();
        }
        var refused = await eng.PostAsJsonAsync($"/api/model-versions/{versionId}/try", new TryModelRequest(ids), Json);
        refused.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ErrorAsync(refused))!.Message.Should().Contain("검출 모델");
    }
}

/// <summary>
/// 진짜 검출 모델로 돌려 좌표가 그림 안에 있고 실제로 무언가를 찾는지 본다.
/// 모델과 사진은 저장소에 없으므로 환경 변수로 줄 때만 돈다 (PrelabelInferenceTests 와 같다).
/// <code>
/// set MLOPS_TEST_DETECTOR=D:\Repo\VMS\VMS.DeepLearning\best.onnx
/// set MLOPS_TEST_IMAGE=D:\Repo\VMS\VMS.DeepLearning\scripts\images\train\1.bmp
/// </code>
/// </summary>
public class ModelTryInferenceTests : IClassFixture<MlopsApiFactory>
{
    private readonly MlopsApiFactory _f;
    public ModelTryInferenceTests(MlopsApiFactory f) => _f = f;

    private static string? DetectorPath => Environment.GetEnvironmentVariable("MLOPS_TEST_DETECTOR");
    private static string? RealImagePath => Environment.GetEnvironmentVariable("MLOPS_TEST_IMAGE");

    /// <summary>
    /// train_dfine.py 가 낸 deploy 규약 D-FINE (images + orig_target_sizes(h, w) → labels · boxes(원본 픽셀 xyxy) · scores)
    /// 과 그 모델의 학습 사진. 크기 입력을 빠뜨리면 세션 실행이 실패한다 — 그 사실을 실제 모델로만 알 수 있었다.
    /// <code>
    /// set MLOPS_TEST_DFINE=…\best.onnx
    /// set MLOPS_TEST_DFINE_IMAGE=…\images\train\xxx.png
    /// </code>
    /// </summary>
    private static string? DFinePath => Environment.GetEnvironmentVariable("MLOPS_TEST_DFINE");
    private static string? DFineImagePath => Environment.GetEnvironmentVariable("MLOPS_TEST_DFINE_IMAGE");

    [Fact]
    public async Task Real_dfine_runs_with_its_size_input_and_boxes_land_inside_the_picture()
    {
        if (DFinePath is not { Length: > 0 } detector || !File.Exists(detector)) return;
        if (DFineImagePath is not { Length: > 0 } picture || !File.Exists(picture)) return;

        var eng = await _f.EngineerAsync();
        var probe = await UploadVersionAsync(eng, (await CreateModelAsync(eng, "try-dfine-probe", TaskType.Detection, ["x"])).Id,
            await File.ReadAllBytesAsync(detector));
        // 클래스 이름은 모델 메타에서 읽힌다 — 실패 응답이 알려 주는 이름으로 계열을 만든다
        var names = (await ErrorAsync(probe))?.Details?.FirstOrDefault(d => d.StartsWith("onnx:"))?[5..].Trim(' ', '[', ']')
            .Split(',', StringSplitOptions.TrimEntries) ?? [];
        names.Should().NotBeEmpty("모델 메타의 names 를 읽어 계열을 맞춘다");

        var model = await CreateModelAsync(eng, "try-real-dfine", TaskType.Detection, names);
        var upload = await UploadVersionAsync(eng, model.Id, await File.ReadAllBytesAsync(detector));
        upload.StatusCode.Should().Be(HttpStatusCode.Created, await upload.Content.ReadAsStringAsync());
        var version = (await upload.Content.ReadFromJsonAsync<ModelVersionDto>(Json))!;
        version.Format.Should().Be(ModelFormat.DFine);

        using var form = new MultipartFormDataContent();
        var real = new ByteArrayContent(await File.ReadAllBytesAsync(picture));
        real.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        form.Add(real, "files", $"dfine-{Guid.NewGuid():N}{Path.GetExtension(picture)}");
        var image = (await (await eng.PostAsync("/api/images", form)).Content.ReadFromJsonAsync<ImageUploadBatchDto>(Json))!.Results[0].Image;

        var res = await eng.PostAsJsonAsync($"/api/model-versions/{version.Id}/try", new TryModelRequest([image.Id]), Json);
        res.StatusCode.Should().Be(HttpStatusCode.OK, await res.Content.ReadAsStringAsync());
        var result = (await res.Content.ReadFromJsonAsync<TryModelResultDto>(Json))!;
        var one = result.Images.Single();

        one.Error.Should().BeNull("D-FINE 은 orig_target_sizes 를 함께 넣어야 돈다");
        result.NmsIou.Should().BeNull("D-FINE 은 서버가 NMS 를 걸지 않는다 — 화면이 런타임처럼 클래스별 NMS 를 건다");
        foreach (var d in one.Detections)
        {
            names.Should().Contain(d.ClassName);
            d.W.Should().BeGreaterThan(0);
            (d.X + d.W).Should().BeLessThanOrEqualTo(1.0001);
            (d.Y + d.H).Should().BeLessThanOrEqualTo(1.0001);
        }

        // 같은 추론을 쓰는 사전 라벨링도 D-FINE 에서 사진을 건너뛰지 않는다 (크기 입력이 빠졌을 때는 전부 Skipped 였다)
        var dataset = (await (await eng.PostAsJsonAsync("/api/datasets",
            new CreateDatasetRequest($"dfine-pre-{Guid.NewGuid():N}"[..16], TaskType.Detection, names), Json))
            .Content.ReadFromJsonAsync<DatasetDto>(Json))!;
        (await eng.PostAsJsonAsync($"/api/datasets/{dataset.Id}/images", new AddImagesRequest([image.Id]), Json)).EnsureSuccessStatusCode();
        var pre = await eng.PostAsJsonAsync($"/api/datasets/{dataset.Id}/prelabel", new PrelabelRequest(version.Id, Confidence: 0.05), Json);
        pre.StatusCode.Should().Be(HttpStatusCode.OK, await pre.Content.ReadAsStringAsync());
        var prelabel = (await pre.Content.ReadFromJsonAsync<PrelabelResultDto>(Json))!;
        prelabel.Skipped.Should().Be(0);
        prelabel.Inferred.Should().Be(1);
    }

    [Fact]
    public async Task Real_detector_finds_something_inside_the_picture()
    {
        if (DetectorPath is not { Length: > 0 } detector || !File.Exists(detector)) return;
        if (RealImagePath is not { Length: > 0 } picture || !File.Exists(picture)) return;

        var eng = await _f.EngineerAsync();
        var model = await CreateModelAsync(eng, "try-real-detector", TaskType.Detection, ["object", "logo"]);
        var upload = await UploadVersionAsync(eng, model.Id, await File.ReadAllBytesAsync(detector),
            new VersionUploadMeta(Classes: ["object", "logo"], License: "AGPL-3.0 확인함"));
        upload.StatusCode.Should().Be(HttpStatusCode.Created, await upload.Content.ReadAsStringAsync());
        var version = (await upload.Content.ReadFromJsonAsync<ModelVersionDto>(Json))!;

        using var form = new MultipartFormDataContent();
        var real = new ByteArrayContent(await File.ReadAllBytesAsync(picture));
        real.Headers.ContentType = new MediaTypeHeaderValue("image/bmp");
        form.Add(real, "files", $"real-{Guid.NewGuid():N}{Path.GetExtension(picture)}");
        var image = (await (await eng.PostAsync("/api/images", form)).Content.ReadFromJsonAsync<ImageUploadBatchDto>(Json))!.Results[0].Image;

        var res = await eng.PostAsJsonAsync($"/api/model-versions/{version.Id}/try", new TryModelRequest([image.Id]), Json);
        res.StatusCode.Should().Be(HttpStatusCode.OK, await res.Content.ReadAsStringAsync());
        var one = (await res.Content.ReadFromJsonAsync<TryModelResultDto>(Json))!.Images.Single();

        // 이 모델의 최고 확신도는 자기 학습 사진에서도 0.16 정도라 기본 문턱(0.25)으로는 아무것도 안 나온다.
        // 바닥값까지 담아 보내므로 화면이 문턱을 낮춰 볼 수 있어야 한다.
        one.Error.Should().BeNull();
        one.Detections.Should().NotBeEmpty("그 모델이 찾아내는 사진이다 — 0 이면 아래 좌표 검사가 헛돈다");
        one.MaxScore.Should().Be(one.Detections.Max(d => d.Score));
        one.Detections.Should().BeInDescendingOrder(d => d.Score);
        foreach (var d in one.Detections)
        {
            new[] { "object", "logo" }.Should().Contain(d.ClassName);
            d.W.Should().BeGreaterThan(0);
            (d.X + d.W).Should().BeLessThanOrEqualTo(1.0001);
            (d.Y + d.H).Should().BeLessThanOrEqualTo(1.0001);
        }
    }
}
