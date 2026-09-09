using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using BODA.VMS.MLOps.Contracts.Datasets;
using BODA.VMS.MLOps.Core.Domain;
using BODA.VMS.MLOps.Server.Auth;
using FluentAssertions;
using SkiaSharp;
using static BODA.VMS.MLOps.Tests.Server.MlopsApiFactory;

namespace BODA.VMS.MLOps.Tests.Server;

/// <summary>
/// 라인 PC 의 NG 이미지 수집 (개발 문서 §5.2).
/// 라인 계정은 사진을 넣기만 하고 큐레이션(태그·삭제)은 못 한다 — 그 경계를 여기서 지킨다.
/// </summary>
public class LineNgUploadTests : IClassFixture<MlopsApiFactory>
{
    private readonly MlopsApiFactory _f;
    public LineNgUploadTests(MlopsApiFactory f) => _f = f;

    private static MultipartFormDataContent Form(string? lineId, string? inspectionId = null, int seed = 1)
    {
        var form = new MultipartFormDataContent();
        var part = new ByteArrayContent(DataManagementApiTests.MakePng(96, 72, SKColors.DimGray, seed));
        part.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        form.Add(part, "files", $"ng-{seed}.png");
        if (lineId is not null) form.Add(new StringContent(lineId), "lineId");
        if (inspectionId is not null) form.Add(new StringContent(inspectionId), "inspectionId");
        return form;
    }

    [Fact]
    public async Task 라인_계정이_NG_이미지를_올릴_수_있다()
    {
        var line = await _f.LineAsync();

        using var form = Form("LINE-A", "INSP-1001", seed: 11);
        var res = await line.PostAsync("/api/images/line-ng", form);

        res.StatusCode.Should().Be(HttpStatusCode.OK, await res.Content.ReadAsStringAsync());
        var batch = (await res.Content.ReadFromJsonAsync<ImageUploadBatchDto>(Json))!;
        batch.Created.Should().Be(1);

        var image = batch.Results[0].Image;
        image.Source.Should().Be(ImageSource.LineNg, "라인이 보낸 것은 출처가 강제되어야 한다");
        image.LineId.Should().Be("LINE-A");
        image.InspectionId.Should().Be("INSP-1001", "생산 이력과 이어 붙일 열쇠다");
    }

    [Fact]
    public async Task lineId_가_없으면_거절한다()
    {
        // 어느 라인에서 온 사진인지 모르면 나중에 걸러 낼 방법이 없다
        var line = await _f.LineAsync();

        using var form = Form(lineId: null, seed: 12);
        var res = await line.PostAsync("/api/images/line-ng", form);

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ErrorAsync(res))!.Message.Should().Contain("lineId");
    }

    [Fact]
    public async Task 올린_뒤에는_출처로_걸러_찾을_수_있다()
    {
        var line = await _f.LineAsync();
        using var form = Form("LINE-B", seed: 13);
        (await line.PostAsync("/api/images/line-ng", form)).EnsureSuccessStatusCode();

        var eng = await _f.EngineerAsync();
        var page = await eng.GetFromJsonAsync<ImagePageDto>("/api/images?source=lineNg&lineId=LINE-B", Json);

        page!.Items.Should().NotBeEmpty();
        page.Items.Should().OnlyContain(i => i.Source == ImageSource.LineNg && i.LineId == "LINE-B");
    }

    [Fact]
    public async Task 라인_계정은_큐레이션까지_할_수_없다()
    {
        var line = await _f.LineAsync();
        using var form = Form("LINE-C", seed: 14);
        var uploaded = (await (await line.PostAsync("/api/images/line-ng", form))
            .Content.ReadFromJsonAsync<ImageUploadBatchDto>(Json))!;
        var id = uploaded.Results[0].Image.Id;

        // 일반 업로드·태그는 라벨러 이상, 삭제는 엔지니어 이상이다
        using var plain = Form("LINE-C", seed: 15);
        (await line.PostAsync("/api/images", plain)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await line.PostAsJsonAsync("/api/images/tags", new TagImagesRequest([id], ["임의"]), Json))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await line.PostAsJsonAsync("/api/images/delete", new DeleteImagesRequest([id]), Json))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task 익명은_올릴_수_없다()
    {
        var anon = _f.CreateClient();
        using var form = Form("LINE-D", seed: 16);
        (await anon.PostAsync("/api/images/line-ng", form)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }
}

/// <summary>
/// 사전 라벨링 (개발 문서 §5.4 Active Learning).
/// 후보 모델이 미리 찍어 준 라벨을 넣되, 사람이 손댄 것은 절대 덮지 않는다.
/// 불확실도는 라벨링 큐의 순서를 정한다.
/// </summary>
public class PrefillTests : IClassFixture<MlopsApiFactory>
{
    private readonly MlopsApiFactory _f;
    public PrefillTests(MlopsApiFactory f) => _f = f;

    private async Task<(HttpClient Engineer, DatasetDto Dataset, Guid[] Images)> ArrangeAsync(int count = 3)
    {
        var eng = await _f.EngineerAsync();

        using var form = new MultipartFormDataContent();
        for (int i = 0; i < count; i++)
        {
            var part = new ByteArrayContent(DataManagementApiTests.MakePng(80, 60, SKColors.SlateGray, 100 + i));
            part.Headers.ContentType = new MediaTypeHeaderValue("image/png");
            form.Add(part, "files", $"pre-{Guid.NewGuid():N}.png");
        }
        var uploaded = (await (await eng.PostAsync("/api/images", form))
            .Content.ReadFromJsonAsync<ImageUploadBatchDto>(Json))!;
        var ids = uploaded.Results.Select(r => r.Image.Id).ToArray();

        var dataset = (await (await eng.PostAsJsonAsync("/api/datasets",
            new CreateDatasetRequest($"prefill-{Guid.NewGuid():N}", TaskType.Detection, ["good", "defect"]), Json))
            .Content.ReadFromJsonAsync<DatasetDto>(Json))!;
        (await eng.PostAsJsonAsync($"/api/datasets/{dataset.Id}/images", new AddImagesRequest(ids), Json))
            .EnsureSuccessStatusCode();

        return (eng, dataset, ids);
    }

    private static AnnotationDto Box(string cls = "defect") =>
        new(AnnotationShape.Box, cls, 0.3, 0.3, 0.2, 0.2);

    [Fact]
    public async Task 미라벨_이미지에_모델_라벨을_채운다()
    {
        var (eng, dataset, ids) = await ArrangeAsync();

        var res = await eng.PostAsJsonAsync($"/api/datasets/{dataset.Id}/prefill",
            new PrefillRequest([new PrefillImageDto(ids[0], [Box()], 0.9)]), Json);

        res.StatusCode.Should().Be(HttpStatusCode.OK, await res.Content.ReadAsStringAsync());
        var result = (await res.Content.ReadFromJsonAsync<PrefillResultDto>(Json))!;
        result.Filled.Should().Be(1);
        result.Received.Should().Be(1);

        var labels = await eng.GetFromJsonAsync<ImageLabelsDto>(
            $"/api/datasets/{dataset.Id}/images/{ids[0]}/labels", Json);
        labels!.Annotations.Should().HaveCount(1);
        labels.Status.Should().Be(LabelStatus.InProgress, "모델이 찍은 것은 '작업 중' 이지 '완료' 가 아니다");
    }

    [Fact]
    public async Task 사람이_이미_라벨한_이미지는_덮지_않는다()
    {
        var (eng, dataset, ids) = await ArrangeAsync();

        // 사람이 먼저 라벨하고 완료로 표시한다
        (await eng.PutAsJsonAsync($"/api/datasets/{dataset.Id}/images/{ids[0]}/labels",
            new SaveLabelsRequest([Box("good")], MarkLabeled: true), Json)).EnsureSuccessStatusCode();

        var res = await eng.PostAsJsonAsync($"/api/datasets/{dataset.Id}/prefill",
            new PrefillRequest([new PrefillImageDto(ids[0], [Box("defect"), Box("defect")], 0.9)]), Json);
        (await res.Content.ReadFromJsonAsync<PrefillResultDto>(Json))!.Filled.Should().Be(0);

        var labels = await eng.GetFromJsonAsync<ImageLabelsDto>(
            $"/api/datasets/{dataset.Id}/images/{ids[0]}/labels", Json);
        labels!.Annotations.Should().HaveCount(1);
        labels.Annotations[0].ClassName.Should().Be("good", "사람이 붙인 라벨이 이겨야 한다");
    }

    [Fact]
    public async Task 불확실도가_큰_이미지가_먼저_큐에_나온다()
    {
        var (eng, dataset, ids) = await ArrangeAsync();

        // 라벨은 붙이지 않고 불확실도만 준다 — 순서만 보는 시험이다
        (await eng.PostAsJsonAsync($"/api/datasets/{dataset.Id}/prefill", new PrefillRequest([
            new PrefillImageDto(ids[0], [], 0.10),
            new PrefillImageDto(ids[1], [], 0.95),
            new PrefillImageDto(ids[2], [], 0.50),
        ]), Json)).EnsureSuccessStatusCode();

        var next = await eng.GetFromJsonAsync<NextImageDto>($"/api/datasets/{dataset.Id}/next-to-label", Json);

        next!.ImageId.Should().Be(ids[1], "가장 애매한 것부터 사람에게 보여 줘야 라벨링 효율이 오른다");
    }

    [Fact]
    public async Task 데이터셋에_없는_이미지는_조용히_건너뛴다()
    {
        var (eng, dataset, _) = await ArrangeAsync();

        var res = await eng.PostAsJsonAsync($"/api/datasets/{dataset.Id}/prefill",
            new PrefillRequest([new PrefillImageDto(Guid.NewGuid(), [Box()], 0.5)]), Json);

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        (await res.Content.ReadFromJsonAsync<PrefillResultDto>(Json))!.Filled.Should().Be(0);
    }

    [Fact]
    public async Task 데이터셋에_없는_클래스는_버린다()
    {
        var (eng, dataset, ids) = await ArrangeAsync();

        (await eng.PostAsJsonAsync($"/api/datasets/{dataset.Id}/prefill",
            new PrefillRequest([new PrefillImageDto(ids[0], [Box("없는클래스"), Box("defect")], 0.5)]), Json))
            .EnsureSuccessStatusCode();

        var labels = await eng.GetFromJsonAsync<ImageLabelsDto>(
            $"/api/datasets/{dataset.Id}/images/{ids[0]}/labels", Json);
        labels!.Annotations.Should().HaveCount(1);
        labels.Annotations[0].ClassName.Should().Be("defect");
    }

    [Fact]
    public async Task 빈_요청은_거절한다()
    {
        var (eng, dataset, _) = await ArrangeAsync(1);

        var res = await eng.PostAsJsonAsync($"/api/datasets/{dataset.Id}/prefill",
            new PrefillRequest([]), Json);

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task 라벨러는_사전_라벨링을_할_수_없다()
    {
        // 모델 결과를 통째로 밀어 넣는 것은 엔지니어나 워커의 일이다
        var (_, dataset, ids) = await ArrangeAsync(1);
        var labeler = await _f.ClientAsAsync("labeler-1", Roles.Labeler);

        var res = await labeler.PostAsJsonAsync($"/api/datasets/{dataset.Id}/prefill",
            new PrefillRequest([new PrefillImageDto(ids[0], [Box()], 0.5)]), Json);

        res.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }
}

/// <summary>
/// 아직 스크립트가 없는 작업 유형(세그멘테이션)을 제출했을 때의 동작.
/// 워커까지 흘러가 "파일 없음" 으로 죽지 않고, 제출 순간에 이유를 말하며 막혀야 한다.
/// </summary>
public class MissingScriptTests : IClassFixture<MlopsApiFactory>
{
    private readonly MlopsApiFactory _f;
    public MissingScriptTests(MlopsApiFactory f) => _f = f;

    [Fact]
    public async Task 스크립트가_없는_작업은_제출_단계에서_막힌다()
    {
        var eng = await _f.EngineerAsync();
        var model = await CreateModelAsync(eng, "seg-model", TaskType.Segmentation, ["good", "defect"]);
        var dataset = await UploadDatasetAsync(eng, "seg-ds", TaskType.Segmentation, "coco");

        var res = await eng.PostAsJsonAsync("/api/training-jobs", new Contracts.Training.CreateTrainingJobRequest(
            dataset.Id, model.Id, TrainingScript.TrainRfdetrSeg), Json);

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var error = (await ErrorAsync(res))!;
        error.Message.Should().Contain("train_rfdetr_seg.py",
            "무엇이 없는지 이름으로 알려 줘야 사람이 조치할 수 있다");
    }

    [Fact]
    public async Task 배포된_스크립트_목록에서_없는_것이_보인다()
    {
        // 워커도 사람도 이 목록을 보고 무엇을 돌릴 수 있는지 판단한다
        var eng = await _f.EngineerAsync();
        var manifest = await eng.GetFromJsonAsync<Contracts.Workers.ScriptsManifest>("/api/workers/scripts", Json);

        manifest!.Scripts.Should().ContainKey("train_dfine.py");
        manifest.Scripts.Should().NotContainKey("train_rfdetr_seg.py");
    }
}
