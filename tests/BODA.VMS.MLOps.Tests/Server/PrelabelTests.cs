using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using BODA.VMS.MLOps.Contracts.Datasets;
using BODA.VMS.MLOps.Contracts.Models;
using BODA.VMS.MLOps.Core.Domain;
using BODA.VMS.MLOps.Server.Auth;
using BODA.VMS.MLOps.Tests.TestAssets;
using FluentAssertions;
using SkiaSharp;
using static BODA.VMS.MLOps.Tests.Server.MlopsApiFactory;

namespace BODA.VMS.MLOps.Tests.Server;

/// <summary>
/// 사전 라벨링 (개발 문서 §5.4 Active Learning) — 모델 없이도 지켜야 하는 경계.
/// 무엇을 거절하는지가 이 기능의 절반이다. 잘못 고른 모델이 조용히 엉뚱한 라벨을 붙이면
/// 사람이 그것을 검토로 승인해 학습 데이터가 오염된다.
/// </summary>
public class PrelabelGuardTests : IClassFixture<MlopsApiFactory>
{
    private readonly MlopsApiFactory _f;
    public PrelabelGuardTests(MlopsApiFactory f) => _f = f;

    private async Task<(HttpClient Engineer, DatasetDto Dataset, ModelVersionDto Version)> ArrangeAsync(
        TaskType datasetTask = TaskType.Detection,
        TaskType modelTask = TaskType.Detection,
        string[]? datasetClasses = null,
        string[]? modelClasses = null)
    {
        var eng = await _f.EngineerAsync();
        var model = await CreateModelAsync(eng, $"pre-{Guid.NewGuid():N}"[..12], modelTask,
            modelClasses ?? ["good", "defect"]);
        var upload = await UploadVersionAsync(eng, model.Id, OnnxStubs.Bytes(OnnxStubs.DeployWithMeta),
            new VersionUploadMeta(Classes: modelClasses ?? ["good", "defect"]));
        upload.EnsureSuccessStatusCode();
        var version = (await upload.Content.ReadFromJsonAsync<ModelVersionDto>(Json))!;

        var datasetResponse = await eng.PostAsJsonAsync("/api/datasets",
            new CreateDatasetRequest($"pre-{Guid.NewGuid():N}"[..12], datasetTask,
                datasetClasses ?? ["good", "defect"]), Json);
        datasetResponse.EnsureSuccessStatusCode();
        var dataset = (await datasetResponse.Content.ReadFromJsonAsync<DatasetDto>(Json))!;

        return (eng, dataset, version);
    }

    private static async Task<Guid[]> AddImagesAsync(HttpClient client, Guid datasetId, int count)
    {
        using var form = new MultipartFormDataContent();
        for (int i = 0; i < count; i++)
        {
            var part = new ByteArrayContent(DataManagementApiTests.MakePng(160, 120, SKColors.SlateGray, 300 + i));
            part.Headers.ContentType = new MediaTypeHeaderValue("image/png");
            form.Add(part, "files", $"pre-{Guid.NewGuid():N}.png");
        }
        var uploaded = (await (await client.PostAsync("/api/images", form))
            .Content.ReadFromJsonAsync<ImageUploadBatchDto>(Json))!;
        var ids = uploaded.Results.Select(r => r.Image.Id).ToArray();
        (await client.PostAsJsonAsync($"/api/datasets/{datasetId}/images", new AddImagesRequest(ids), Json))
            .EnsureSuccessStatusCode();
        return ids;
    }

    [Fact]
    public async Task 검출이_아닌_데이터셋은_아직_지원하지_않는다고_말한다()
    {
        // 조용히 아무것도 안 하는 것보다 왜 안 되는지 말하는 편이 낫다.
        // (모델 유형이 데이터셋과 다른 경우도 막지만, 분류 모델 아티팩트가 있어야 만들 수 있어
        //  여기서는 재현하지 않는다 — 레지스트리가 업로드 단계에서 이미 규약·유형을 검사한다.)
        var (eng, dataset, version) = await ArrangeAsync(datasetTask: TaskType.Classification);

        var res = await eng.PostAsJsonAsync($"/api/datasets/{dataset.Id}/prelabel",
            new PrelabelRequest(version.Id), Json);

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ErrorAsync(res))!.Message.Should().Contain("검출");
    }

    [Fact]
    public async Task 클래스가_하나도_안_겹치면_거절한다()
    {
        // 이름이 다른 모델을 잘못 고른 것이다. 통과시키면 라벨이 하나도 안 붙고 이유도 모른다.
        var (eng, dataset, version) = await ArrangeAsync(
            datasetClasses: ["스크래치", "찍힘"], modelClasses: ["good", "defect"]);
        await AddImagesAsync(eng, dataset.Id, 1);

        var res = await eng.PostAsJsonAsync($"/api/datasets/{dataset.Id}/prelabel",
            new PrelabelRequest(version.Id), Json);

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var error = (await ErrorAsync(res))!;
        error.Code.Should().Be(Contracts.ErrorCodes.ClassMismatch);
        error.Message.Should().Contain("겹치지");
    }

    [Fact]
    public async Task 미라벨_이미지가_없으면_그렇게_말한다()
    {
        var (eng, dataset, version) = await ArrangeAsync();

        var res = await eng.PostAsJsonAsync($"/api/datasets/{dataset.Id}/prelabel",
            new PrelabelRequest(version.Id), Json);

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var result = (await res.Content.ReadFromJsonAsync<PrelabelResultDto>(Json))!;
        result.Considered.Should().Be(0);
        result.Message.Should().Contain("없습니다");
    }

    [Fact]
    public async Task 없는_모델_버전은_404_다()
    {
        var (eng, dataset, _) = await ArrangeAsync();

        var res = await eng.PostAsJsonAsync($"/api/datasets/{dataset.Id}/prelabel",
            new PrelabelRequest(Guid.NewGuid()), Json);

        res.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task 라벨러는_사전_라벨링을_돌릴_수_없다()
    {
        // 모델 하나로 데이터셋 전체에 라벨을 붙이는 일이라 엔지니어의 몫이다
        var (_, dataset, version) = await ArrangeAsync();
        var labeler = await _f.ClientAsAsync("labeler-pre", Roles.Labeler);

        var res = await labeler.PostAsJsonAsync($"/api/datasets/{dataset.Id}/prelabel",
            new PrelabelRequest(version.Id), Json);

        res.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }
}

/// <summary>
/// 실제로 학습된 검출 모델을 돌려 본다.
///
/// <para>
/// 좌표 규약이 어긋나도 화면에는 "그럴듯한" 상자가 뜨고, 사람이 검토로 승인해 버리면
/// 그 오류가 다음 학습 데이터가 된다. 그래서 진짜 모델로 한 번은 돌려 봐야 한다.
/// 모델 파일은 저장소에 없으므로 환경 변수로 줄 때만 돈다.
/// </para>
/// <code>
/// set MLOPS_TEST_DETECTOR=D:\Repo\VMS\VMS.DeepLearning\best.onnx
/// dotnet test --filter PrelabelInferenceTests
/// </code>
/// </summary>
public class PrelabelInferenceTests : IClassFixture<MlopsApiFactory>
{
    private readonly MlopsApiFactory _f;
    public PrelabelInferenceTests(MlopsApiFactory f) => _f = f;

    private static string? DetectorPath => Environment.GetEnvironmentVariable("MLOPS_TEST_DETECTOR");

    /// <summary>
    /// 그 모델이 실제로 무언가를 찾아내는 사진. 없으면 합성 사진으로 도는데,
    /// 검출이 0 이면 좌표 검사가 헛돌기 때문에 진짜 사진을 함께 주는 편이 낫다.
    /// </summary>
    private static string? RealImagePath => Environment.GetEnvironmentVariable("MLOPS_TEST_IMAGE");

    private static bool Available => DetectorPath is { Length: > 0 } p && File.Exists(p);
    private static bool HasRealImage => RealImagePath is { Length: > 0 } p && File.Exists(p);

    /// <summary>어두운 바탕에 밝은 사각형 하나 — 모델이 무엇을 찾든 좌표가 그림 안이면 된다.</summary>
    private static byte[] Scene(int seed)
    {
        using var bitmap = new SKBitmap(640, 480);
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(new SKColor(30, 32, 38));
            using var paint = new SKPaint { Color = new SKColor((byte)(60 + seed * 30 % 180), 150, 220) };
            canvas.DrawRect(SKRect.Create(80 + seed * 20, 60, 220, 180), paint);
            using var text = new SKPaint { Color = SKColors.White, TextSize = 40 };
            canvas.DrawText($"SAMPLE {seed}", 90, 380, text);
        }
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    [Fact]
    public async Task 진짜_모델로_돌려_라벨과_불확실도를_채운다()
    {
        if (!Available) return;

        var eng = await _f.EngineerAsync();
        var admin = await _f.AdminAsync();

        // 모델의 클래스 이름을 그대로 데이터셋 클래스로 쓴다 (겹치지 않으면 서버가 거절한다)
        var onnx = await File.ReadAllBytesAsync(DetectorPath!);
        var model = await CreateModelAsync(eng, "real-detector", TaskType.Detection, ["object", "logo"]);
        var upload = await UploadVersionAsync(eng, model.Id, onnx,
            new VersionUploadMeta(Classes: ["object", "logo"], License: "AGPL-3.0 확인함"));
        upload.StatusCode.Should().Be(HttpStatusCode.Created, await upload.Content.ReadAsStringAsync());
        var version = (await upload.Content.ReadFromJsonAsync<ModelVersionDto>(Json))!;
        version.Format.Should().Be(ModelFormat.Yolo);

        var dataset = (await (await eng.PostAsJsonAsync("/api/datasets",
            new CreateDatasetRequest("real-prelabel", TaskType.Detection, ["object", "logo"]), Json))
            .Content.ReadFromJsonAsync<DatasetDto>(Json))!;

        using var form = new MultipartFormDataContent();
        for (int i = 0; i < 3; i++)
        {
            var part = new ByteArrayContent(Scene(i));
            part.Headers.ContentType = new MediaTypeHeaderValue("image/png");
            form.Add(part, "files", $"scene-{i}-{Guid.NewGuid():N}.png");
        }
        if (HasRealImage)
        {
            var real = new ByteArrayContent(await File.ReadAllBytesAsync(RealImagePath!));
            real.Headers.ContentType = new MediaTypeHeaderValue("image/bmp");
            form.Add(real, "files", $"real-{Guid.NewGuid():N}{Path.GetExtension(RealImagePath)}");
        }
        var uploaded = (await (await eng.PostAsync("/api/images", form))
            .Content.ReadFromJsonAsync<ImageUploadBatchDto>(Json))!;
        var ids = uploaded.Results.Select(r => r.Image.Id).ToArray();
        (await eng.PostAsJsonAsync($"/api/datasets/{dataset.Id}/images", new AddImagesRequest(ids), Json))
            .EnsureSuccessStatusCode();

        var res = await eng.PostAsJsonAsync($"/api/datasets/{dataset.Id}/prelabel",
            new PrelabelRequest(version.Id, Confidence: 0.10), Json);
        res.StatusCode.Should().Be(HttpStatusCode.OK, await res.Content.ReadAsStringAsync());
        var result = (await res.Content.ReadFromJsonAsync<PrelabelResultDto>(Json))!;

        int expected = HasRealImage ? 4 : 3;
        result.Considered.Should().Be(expected);
        result.Inferred.Should().Be(expected, "모든 장의 추론이 끝나야 한다");
        result.Skipped.Should().Be(0);
        result.Filled.Should().Be(expected, "불확실도는 라벨이 없어도 기록된다");
        if (HasRealImage)
        {
            result.Annotations.Should().BeGreaterThan(0,
                "그 모델이 찾아내는 사진을 넣었으므로 라벨이 하나는 붙어야 한다 — " +
                "0 이면 아래 좌표 검사가 헛돈다");
        }

        // 붙은 라벨이 있다면 좌표가 그림 안에 있어야 한다 — 여기가 어긋나면 화면에서 알아채기 어렵다
        foreach (var id in ids)
        {
            var labels = await eng.GetFromJsonAsync<ImageLabelsDto>(
                $"/api/datasets/{dataset.Id}/images/{id}/labels", Json);
            foreach (var a in labels!.Annotations)
            {
                a.Shape.Should().Be(AnnotationShape.Box);
                a.X.Should().BeInRange(0, 1);
                a.Y.Should().BeInRange(0, 1);
                (a.X!.Value + a.W!.Value).Should().BeLessThanOrEqualTo(1.0001);
                (a.Y!.Value + a.H!.Value).Should().BeLessThanOrEqualTo(1.0001);
                a.W!.Value.Should().BeGreaterThan(0);
                new[] { "object", "logo" }.Should().Contain(a.ClassName);
            }
        }

        // 불확실도가 기록됐으면 큐가 그 순서로 나온다
        var next = await eng.GetFromJsonAsync<NextImageDto>($"/api/datasets/{dataset.Id}/next-to-label", Json);
        next!.ImageId.Should().NotBeNull();
        ids.Should().Contain(next.ImageId!.Value);
    }

    [Fact]
    public async Task 두_번_돌려도_사람이_손댄_것은_덮지_않는다()
    {
        if (!Available) return;

        var eng = await _f.EngineerAsync();
        var onnx = await File.ReadAllBytesAsync(DetectorPath!);
        var model = await CreateModelAsync(eng, "real-twice", TaskType.Detection, ["object", "logo"]);
        var version = (await (await UploadVersionAsync(eng, model.Id, onnx,
            new VersionUploadMeta(Classes: ["object", "logo"], License: "AGPL-3.0 확인함")))
            .Content.ReadFromJsonAsync<ModelVersionDto>(Json))!;

        var dataset = (await (await eng.PostAsJsonAsync("/api/datasets",
            new CreateDatasetRequest("real-twice", TaskType.Detection, ["object", "logo"]), Json))
            .Content.ReadFromJsonAsync<DatasetDto>(Json))!;

        using var form = new MultipartFormDataContent();
        var part = new ByteArrayContent(Scene(7));
        part.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        form.Add(part, "files", $"twice-{Guid.NewGuid():N}.png");
        var uploaded = (await (await eng.PostAsync("/api/images", form))
            .Content.ReadFromJsonAsync<ImageUploadBatchDto>(Json))!;
        var imageId = uploaded.Results[0].Image.Id;
        (await eng.PostAsJsonAsync($"/api/datasets/{dataset.Id}/images", new AddImagesRequest([imageId]), Json))
            .EnsureSuccessStatusCode();

        // 사람이 먼저 라벨을 붙이고 완료로 표시한다
        (await eng.PutAsJsonAsync($"/api/datasets/{dataset.Id}/images/{imageId}/labels",
            new SaveLabelsRequest([new AnnotationDto(AnnotationShape.Box, "object", 0.4, 0.4, 0.1, 0.1)],
                MarkLabeled: true), Json)).EnsureSuccessStatusCode();

        var res = await eng.PostAsJsonAsync($"/api/datasets/{dataset.Id}/prelabel",
            new PrelabelRequest(version.Id, Confidence: 0.10), Json);
        res.EnsureSuccessStatusCode();
        var result = (await res.Content.ReadFromJsonAsync<PrelabelResultDto>(Json))!;

        result.Considered.Should().Be(0, "이미 라벨한 이미지는 대상이 아니다");

        var labels = await eng.GetFromJsonAsync<ImageLabelsDto>(
            $"/api/datasets/{dataset.Id}/images/{imageId}/labels", Json);
        labels!.Annotations.Should().HaveCount(1);
        labels.Annotations[0].X.Should().BeApproximately(0.4, 1e-6, "사람이 붙인 라벨이 그대로 있어야 한다");
    }
}
