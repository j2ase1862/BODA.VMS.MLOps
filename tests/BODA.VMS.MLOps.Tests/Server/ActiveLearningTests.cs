using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using BODA.VMS.MLOps.Contracts;
using BODA.VMS.MLOps.Contracts.Datasets;
using BODA.VMS.MLOps.Contracts.Models;
using BODA.VMS.MLOps.Core.Domain;
using BODA.VMS.MLOps.Server.Services;
using BODA.VMS.MLOps.Tests.TestAssets;
using FluentAssertions;
using SkiaSharp;
using static BODA.VMS.MLOps.Tests.Server.MlopsApiFactory;

namespace BODA.VMS.MLOps.Tests.Server;

/// <summary>
/// 새 사진 훑기 (Active Learning, 개발 문서 §5.5 4번) — 데이터셋에 없는 수집 사진을 후보 모델로 훑는다.
/// 훑기는 아무것도 담지 않는다. 사람이 골라 담고, 고른 것만 미리 채운다.
/// </summary>
public class ActiveLearningTests : IClassFixture<MlopsApiFactory>
{
    private readonly MlopsApiFactory _f;
    public ActiveLearningTests(MlopsApiFactory f) => _f = f;

    private async Task<(HttpClient Eng, DatasetDto Dataset, ModelVersionDto Version)> ArrangeAsync(
        TaskType datasetTask = TaskType.Detection, string[]? datasetClasses = null)
    {
        var eng = await _f.EngineerAsync();
        var model = await CreateModelAsync(eng, $"al-{Guid.NewGuid():N}"[..12], TaskType.Detection, ["good", "defect"]);
        var upload = await UploadVersionAsync(eng, model.Id, OnnxStubs.Bytes(OnnxStubs.YoloStub),
            new VersionUploadMeta(Classes: ["good", "defect"], License: "AGPL-3.0 확인함"));
        upload.StatusCode.Should().Be(HttpStatusCode.Created, await upload.Content.ReadAsStringAsync());
        var version = (await upload.Content.ReadFromJsonAsync<ModelVersionDto>(Json))!;

        var dataset = (await (await eng.PostAsJsonAsync("/api/datasets",
            new CreateDatasetRequest($"al-{Guid.NewGuid():N}"[..12], datasetTask, datasetClasses ?? ["good", "defect"]), Json))
            .Content.ReadFromJsonAsync<DatasetDto>(Json))!;
        return (eng, dataset, version);
    }

    private static Task<HttpResponseMessage> ScanAsync(HttpClient c, Guid datasetId, ActiveLearningScanRequest req) =>
        c.PostAsJsonAsync($"/api/datasets/{datasetId}/active-learning/scan", req, Json);

    [Fact]
    public async Task Scans_only_pool_pictures_not_yet_in_the_dataset_and_adds_nothing()
    {
        var (eng, dataset, version) = await ArrangeAsync();
        var ids = await ModelTryTests.UploadImagesAsync(eng, 3, seed: 900);
        (await eng.PostAsJsonAsync($"/api/datasets/{dataset.Id}/images", new AddImagesRequest([ids[0]]), Json)).EnsureSuccessStatusCode();

        var res = await ScanAsync(eng, dataset.Id, new ActiveLearningScanRequest(version.Id, Take: ActiveLearningService.MaxPerScan));
        res.StatusCode.Should().Be(HttpStatusCode.OK, await res.Content.ReadAsStringAsync());
        var result = (await res.Content.ReadFromJsonAsync<ActiveLearningScanResultDto>(Json))!;

        var scanned = result.Items.Select(i => i.Image.Id).ToList();
        scanned.Should().NotContain(ids[0], "이미 데이터셋에 있는 사진은 훑지 않는다");
        scanned.Should().Contain([ids[1], ids[2]]);
        result.Items.Should().OnlyContain(i => i.Error == null);
        result.NextSkip.Should().Be(result.Scanned);
        result.ModelClasses.Should().Equal("good", "defect");

        // 훑기만 했다 — 데이터셋은 그대로 한 장
        (await eng.GetFromJsonAsync<DatasetDto>($"/api/datasets/{dataset.Id}", Json))!.Stats!.ImageCount.Should().Be(1);
    }

    [Fact]
    public async Task Pages_through_the_pool_in_batches()
    {
        var (eng, dataset, version) = await ArrangeAsync();
        await ModelTryTests.UploadImagesAsync(eng, 2, seed: 920);

        var first = (await (await ScanAsync(eng, dataset.Id, new ActiveLearningScanRequest(version.Id, Take: 1)))
            .Content.ReadFromJsonAsync<ActiveLearningScanResultDto>(Json))!;
        first.Scanned.Should().Be(1);
        first.NextSkip.Should().Be(1);
        first.PoolTotal.Should().BeGreaterThanOrEqualTo(2);

        var second = (await (await ScanAsync(eng, dataset.Id, new ActiveLearningScanRequest(version.Id, Skip: first.NextSkip, Take: 1)))
            .Content.ReadFromJsonAsync<ActiveLearningScanResultDto>(Json))!;
        second.Items.Single().Image.Id.Should().NotBe(first.Items.Single().Image.Id, "다음 묶음은 다른 사진이다");
    }

    [Fact]
    public async Task Refuses_what_it_cannot_scan()
    {
        var (eng, classification, version) = await ArrangeAsync(TaskType.Classification, ["good", "defect"]);
        (await ScanAsync(eng, classification.Id, new ActiveLearningScanRequest(version.Id))).StatusCode.Should().Be(HttpStatusCode.BadRequest);

        // 이름이 하나도 안 겹치는 모델 — 골라 담아도 미리 채울 라벨이 없다
        var (_, other, otherVersion) = await ArrangeAsync(TaskType.Detection, ["scratch"]);
        var mismatch = await ScanAsync(eng, other.Id, new ActiveLearningScanRequest(otherVersion.Id));
        mismatch.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ErrorAsync(mismatch))!.Code.Should().Be(ErrorCodes.ClassMismatch);

        var labeler = await _f.ClientAsAsync("labeler-al", "Labeler");
        (await ScanAsync(labeler, other.Id, new ActiveLearningScanRequest(otherVersion.Id))).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Prelabel_can_be_limited_to_the_pictures_picked_from_a_scan()
    {
        // 훑기에서 고른 사진만 미리 채운다 — 원래 있던 미라벨 사진까지 건드리면 사람이 정하지 않은 라벨이 붙는다
        var (eng, dataset, version) = await ArrangeAsync();
        var ids = await ModelTryTests.UploadImagesAsync(eng, 3, seed: 940);
        (await eng.PostAsJsonAsync($"/api/datasets/{dataset.Id}/images", new AddImagesRequest(ids), Json)).EnsureSuccessStatusCode();

        var res = await eng.PostAsJsonAsync($"/api/datasets/{dataset.Id}/prelabel",
            new PrelabelRequest(version.Id, Confidence: 0.25, ImageIds: [ids[1]]), Json);
        res.StatusCode.Should().Be(HttpStatusCode.OK, await res.Content.ReadAsStringAsync());
        var result = (await res.Content.ReadFromJsonAsync<PrelabelResultDto>(Json))!;
        result.Considered.Should().Be(1);
        result.Inferred.Should().Be(1);
    }
}

/// <summary>
/// 진짜 검출 모델로 훑어 "자신 없어 한 순서" 가 맞는지 본다 — 후보 없는 사진이 앞, 찾은 사진이 뒤.
/// 모델과 사진은 저장소에 없으므로 환경 변수로 줄 때만 돈다 (ModelTryInferenceTests 와 같다).
/// </summary>
public class ActiveLearningInferenceTests : IClassFixture<MlopsApiFactory>
{
    private readonly MlopsApiFactory _f;
    public ActiveLearningInferenceTests(MlopsApiFactory f) => _f = f;

    [Fact]
    public async Task Pictures_the_model_is_least_sure_about_come_first()
    {
        if (Environment.GetEnvironmentVariable("MLOPS_TEST_DETECTOR") is not { Length: > 0 } detector || !File.Exists(detector)) return;
        if (Environment.GetEnvironmentVariable("MLOPS_TEST_IMAGE") is not { Length: > 0 } picture || !File.Exists(picture)) return;

        var eng = await _f.EngineerAsync();
        var model = await CreateModelAsync(eng, "al-real-detector", TaskType.Detection, ["object", "logo"]);
        var upload = await UploadVersionAsync(eng, model.Id, await File.ReadAllBytesAsync(detector),
            new VersionUploadMeta(Classes: ["object", "logo"], License: "AGPL-3.0 확인함"));
        upload.StatusCode.Should().Be(HttpStatusCode.Created, await upload.Content.ReadAsStringAsync());
        var version = (await upload.Content.ReadFromJsonAsync<ModelVersionDto>(Json))!;
        var dataset = (await (await eng.PostAsJsonAsync("/api/datasets",
            new CreateDatasetRequest("al-real", TaskType.Detection, ["object", "logo"]), Json)).Content.ReadFromJsonAsync<DatasetDto>(Json))!;

        // 모델이 로고를 찾는 사진 하나와, 아무것도 못 볼 합성 사진 둘
        using var form = new MultipartFormDataContent();
        var real = new ByteArrayContent(await File.ReadAllBytesAsync(picture));
        real.Headers.ContentType = new MediaTypeHeaderValue("image/bmp");
        form.Add(real, "files", $"al-real-{Guid.NewGuid():N}{Path.GetExtension(picture)}");
        for (int i = 0; i < 2; i++)
        {
            var part = new ByteArrayContent(DataManagementApiTests.MakePng(320, 240, SKColors.Black, 980 + i));
            part.Headers.ContentType = new MediaTypeHeaderValue("image/png");
            form.Add(part, "files", $"al-blank-{i}-{Guid.NewGuid():N}.png");
        }
        var uploaded = (await (await eng.PostAsync("/api/images", form)).Content.ReadFromJsonAsync<ImageUploadBatchDto>(Json))!;
        var realId = uploaded.Results[0].Image.Id;

        var res = await eng.PostAsJsonAsync($"/api/datasets/{dataset.Id}/active-learning/scan",
            new ActiveLearningScanRequest(version.Id, Take: ActiveLearningService.MaxPerScan), Json);
        res.StatusCode.Should().Be(HttpStatusCode.OK, await res.Content.ReadAsStringAsync());
        var items = (await res.Content.ReadFromJsonAsync<ActiveLearningScanResultDto>(Json))!.Items.ToList();

        var hit = items.Single(i => i.Image.Id == realId);
        hit.MaxScore.Should().NotBeNull("그 모델이 찾아내는 사진이다");
        // 자신 없어 한 순서 — 점수 없는 것이 앞, 그다음 최고 점수 오름차순
        items.Select(i => i.MaxScore ?? -1).Should().BeInAscendingOrder();
        items.IndexOf(hit).Should().BeGreaterThan(items.FindIndex(i => i.MaxScore is null));
    }
}
