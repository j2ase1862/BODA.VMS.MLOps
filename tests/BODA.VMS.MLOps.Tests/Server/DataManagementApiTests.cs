using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using BODA.VMS.MLOps.Contracts;
using BODA.VMS.MLOps.Contracts.Datasets;
using BODA.VMS.MLOps.Core.Domain;
using FluentAssertions;
using SkiaSharp;
using static BODA.VMS.MLOps.Tests.Server.MlopsApiFactory;

namespace BODA.VMS.MLOps.Tests.Server;

/// <summary>
/// 데이터 관리·라벨링 한 바퀴 (개발 문서 §5.2·§5.4):
/// 이미지 업로드 → 데이터셋 구성 → 라벨 저장 → 검토 → 스냅샷 → 내보내기 zip.
/// 마지막에 zip 을 실제로 열어 학습 스크립트가 읽는 구조인지 확인한다.
/// </summary>
public class DataManagementApiTests : IClassFixture<MlopsApiFactory>
{
    private readonly MlopsApiFactory _f;
    public DataManagementApiTests(MlopsApiFactory f) => _f = f;

    /// <summary>실제로 디코딩되는 PNG 를 만든다 — 서버가 크기·썸네일·해시를 뽑아야 하기 때문</summary>
    public static byte[] MakePng(int width, int height, SKColor color, int seed = 0)
    {
        using var bitmap = new SKBitmap(width, height);
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(color);
            using var paint = new SKPaint { Color = new SKColor((byte)(seed * 37 % 255), (byte)(seed * 91 % 255), 20) };
            canvas.DrawRect(SKRect.Create(seed % Math.Max(1, width - 10), 5, 20, 20), paint);
        }
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
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
    public async Task Upload_stores_once_per_file_and_reports_near_duplicates()
    {
        var eng = await _f.EngineerAsync();
        var bytes = MakePng(320, 240, SKColors.SlateGray, seed: 1);

        var first = await UploadAsync(eng, ("a.png", bytes));
        first.Created.Should().Be(1);
        var image = first.Results[0].Image;
        image.Width.Should().Be(320);
        image.Height.Should().Be(240);
        image.PerceptualHash.Should().NotBeNullOrEmpty();
        image.ThumbnailUrl.Should().Contain("/thumb");

        // 같은 파일을 다시 올리면 새 레코드를 만들지 않는다
        var again = await UploadAsync(eng, ("a-copy.png", bytes));
        again.Created.Should().Be(0);
        again.Duplicates.Should().Be(1);
        again.Results[0].Image.Id.Should().Be(image.Id);

        // 썸네일·축소본·원본이 모두 나온다
        foreach (var url in new[] { image.ThumbnailUrl, image.ViewUrl, image.OriginalUrl })
        {
            var res = await eng.GetAsync(url);
            res.StatusCode.Should().Be(HttpStatusCode.OK, url);
            (await res.Content.ReadAsByteArrayAsync()).Length.Should().BeGreaterThan(0);
        }
    }

    [Fact]
    public async Task Non_image_upload_is_rejected_with_a_clear_reason()
    {
        var eng = await _f.EngineerAsync();
        using var form = new MultipartFormDataContent();
        form.Add(new ByteArrayContent("not an image"u8.ToArray()), "files", "bad.png");
        var res = await eng.PostAsync("/api/images", form);

        // 한 장이 잘못돼도 요청 자체는 성공하고 errors 로 알려 준다
        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var batch = (await res.Content.ReadFromJsonAsync<ImageUploadBatchDto>(Json))!;
        batch.Created.Should().Be(0);
        batch.Errors.Should().ContainSingle().Which.Should().Contain("bad.png");
    }

    [Fact]
    public async Task Detection_round_trip_from_upload_to_export()
    {
        var eng = await _f.EngineerAsync();
        var dataset = (await (await eng.PostAsJsonAsync("/api/datasets",
            new CreateDatasetRequest("검출 데이터셋", TaskType.Detection, ["good", "defect"]), Json))
            .Content.ReadFromJsonAsync<DatasetDto>(Json))!;

        var upload = await UploadAsync(eng,
            ("d1.png", MakePng(640, 480, SKColors.White, 11)),
            ("d2.png", MakePng(640, 480, SKColors.Black, 22)));
        upload.Created.Should().Be(2);
        var ids = upload.Results.Select(r => r.Image.Id).ToArray();

        (await eng.PostAsJsonAsync($"/api/datasets/{dataset.Id}/images",
            new AddImagesRequest(ids, DatasetSplit.Train), Json)).StatusCode.Should().Be(HttpStatusCode.OK);

        // 라벨 저장 — 좌표는 0~1 정규화
        var labels = new SaveLabelsRequest([
            new AnnotationDto(AnnotationShape.Box, "defect", 0.1, 0.2, 0.4, 0.2),
        ], MarkLabeled: true);
        var saved = (await (await eng.PutAsJsonAsync($"/api/datasets/{dataset.Id}/images/{ids[0]}/labels", labels, Json))
            .Content.ReadFromJsonAsync<ImageLabelsDto>(Json))!;
        saved.Status.Should().Be(LabelStatus.Labeled);
        saved.Annotations.Should().ContainSingle().Which.ClassName.Should().Be("defect");

        (await eng.PutAsJsonAsync($"/api/datasets/{dataset.Id}/images/{ids[1]}/labels",
            new SaveLabelsRequest([new AnnotationDto(AnnotationShape.Box, "good", 0.3, 0.3, 0.2, 0.2)], true), Json))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        (await eng.PostAsJsonAsync($"/api/datasets/{dataset.Id}/split",
            new SetSplitRequest([ids[1]], DatasetSplit.Val), Json)).StatusCode.Should().Be(HttpStatusCode.OK);

        var stats = (await eng.GetFromJsonAsync<DatasetDto>($"/api/datasets/{dataset.Id}", Json))!.Stats!;
        stats.ImageCount.Should().Be(2);
        stats.Labeled.Should().Be(2);
        stats.TrainCount.Should().Be(1);
        stats.ValCount.Should().Be(1);
        stats.PerClass.Should().ContainKeys("good", "defect");

        // 스냅샷 → 내보내기
        var version = (await (await eng.PostAsJsonAsync($"/api/datasets/{dataset.Id}/versions",
            new CreateSnapshotRequest("v1"), Json)).Content.ReadFromJsonAsync<DatasetVersionDto>(Json))!;
        version.Source.Should().Be(DatasetVersionSource.Snapshot);
        version.ExportFormat.Should().Be("yolo");
        version.ImageCount.Should().Be(2);
        version.AnnotationCount.Should().Be(2);
        version.ExportReady.Should().BeFalse("스냅샷 zip 은 처음 요청될 때 만들어진다");

        var export = await eng.GetAsync($"/api/dataset-versions/{version.Id}/export");
        export.StatusCode.Should().Be(HttpStatusCode.OK);
        var entries = await ReadZipAsync(export);

        entries.Keys.Should().Contain("data.yaml");
        entries["data.yaml"].Should().Contain("0: good").And.Contain("1: defect").And.Contain("train: images/train");
        entries.Keys.Should().Contain(k => k.StartsWith("images/train/") && k.EndsWith(".png"));
        entries.Keys.Should().Contain(k => k.StartsWith("images/val/"));

        var trainLabel = entries.First(e => e.Key.StartsWith("labels/train/")).Value.Trim();
        trainLabel.Should().Be("1 0.3 0.3 0.4 0.2");

        // 두 번째 요청은 만들어 둔 zip 을 그대로 준다
        (await eng.GetFromJsonAsync<DatasetVersionDto>($"/api/dataset-versions/{version.Id}", Json))!
            .ExportReady.Should().BeTrue();
    }

    [Fact]
    public async Task Classification_export_uses_class_folders()
    {
        var eng = await _f.EngineerAsync();
        var dataset = (await (await eng.PostAsJsonAsync("/api/datasets",
            new CreateDatasetRequest("분류 데이터셋", TaskType.Classification, ["ok", "ng"]), Json))
            .Content.ReadFromJsonAsync<DatasetDto>(Json))!;

        var upload = await UploadAsync(eng, ("c1.png", MakePng(256, 256, SKColors.Red, 31)));
        var imageId = upload.Results[0].Image.Id;
        await eng.PostAsJsonAsync($"/api/datasets/{dataset.Id}/images", new AddImagesRequest([imageId]), Json);

        // 검출용 도형은 분류 데이터셋에서 거부된다
        var wrongShape = await eng.PutAsJsonAsync($"/api/datasets/{dataset.Id}/images/{imageId}/labels",
            new SaveLabelsRequest([new AnnotationDto(AnnotationShape.Box, "ok", 0.1, 0.1, 0.2, 0.2)]), Json);
        wrongShape.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ErrorAsync(wrongShape))!.Details.Should().Contain(d => d.Contains("Box"));

        // 데이터셋에 없는 클래스도 거부된다
        var wrongClass = await eng.PutAsJsonAsync($"/api/datasets/{dataset.Id}/images/{imageId}/labels",
            new SaveLabelsRequest([new AnnotationDto(AnnotationShape.Classification, "없는클래스")]), Json);
        wrongClass.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        await eng.PutAsJsonAsync($"/api/datasets/{dataset.Id}/images/{imageId}/labels",
            new SaveLabelsRequest([new AnnotationDto(AnnotationShape.Classification, "ng")], true), Json);

        var version = (await (await eng.PostAsJsonAsync($"/api/datasets/{dataset.Id}/versions",
            new CreateSnapshotRequest(), Json)).Content.ReadFromJsonAsync<DatasetVersionDto>(Json))!;
        var entries = await ReadZipAsync(await eng.GetAsync($"/api/dataset-versions/{version.Id}/export"));
        entries.Keys.Should().Contain(k => k.StartsWith("train/ng/"));
    }

    [Fact]
    public async Task Anomaly_dataset_requires_a_normal_class()
    {
        var eng = await _f.EngineerAsync();
        var bad = await eng.PostAsJsonAsync("/api/datasets",
            new CreateDatasetRequest("이상탐지", TaskType.Anomaly, ["scratch", "dent"]), Json);
        bad.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ErrorAsync(bad))!.Message.Should().Contain("정상 클래스");

        (await eng.PostAsJsonAsync("/api/datasets",
            new CreateDatasetRequest("이상탐지", TaskType.Anomaly, ["good", "scratch"]), Json))
            .StatusCode.Should().Be(HttpStatusCode.Created);
    }

    [Fact]
    public async Task Locking_stops_two_people_editing_the_same_image()
    {
        var eng = await _f.EngineerAsync();
        var other = await _f.ClientAsAsync("labeler-2", "Labeler");
        var dataset = (await (await eng.PostAsJsonAsync("/api/datasets",
            new CreateDatasetRequest("잠금 시험", TaskType.Detection, ["good"]), Json))
            .Content.ReadFromJsonAsync<DatasetDto>(Json))!;
        var imageId = (await UploadAsync(eng, ("lock.png", MakePng(200, 200, SKColors.Blue, 41)))).Results[0].Image.Id;
        await eng.PostAsJsonAsync($"/api/datasets/{dataset.Id}/images", new AddImagesRequest([imageId]), Json);

        var locked = (await (await eng.PostAsync($"/api/datasets/{dataset.Id}/images/{imageId}/lock", null))
            .Content.ReadFromJsonAsync<ImageLabelsDto>(Json))!;
        locked.LockedByMe.Should().BeTrue();
        locked.LockedBy.Should().Be("engineer");

        // 남이 잠근 이미지는 잠글 수도 저장할 수도 없다
        (await other.PostAsync($"/api/datasets/{dataset.Id}/images/{imageId}/lock", null))
            .StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await other.PutAsJsonAsync($"/api/datasets/{dataset.Id}/images/{imageId}/labels",
            new SaveLabelsRequest([]), Json)).StatusCode.Should().Be(HttpStatusCode.Conflict);

        // 잠근 사람은 그대로 저장할 수 있다
        (await eng.PutAsJsonAsync($"/api/datasets/{dataset.Id}/images/{imageId}/labels",
            new SaveLabelsRequest([new AnnotationDto(AnnotationShape.Box, "good", 0.1, 0.1, 0.3, 0.3)], true), Json))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        (await eng.PostAsync($"/api/datasets/{dataset.Id}/images/{imageId}/unlock", null))
            .StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await other.PostAsync($"/api/datasets/{dataset.Id}/images/{imageId}/lock", null))
            .StatusCode.Should().Be(HttpStatusCode.OK, "잠금을 풀면 다른 사람이 이어받는다");
    }

    [Fact]
    public async Task Expired_lock_is_taken_over_by_the_next_person()
    {
        var eng = await _f.EngineerAsync();
        var other = await _f.ClientAsAsync("labeler-3", "Labeler");
        var dataset = (await (await eng.PostAsJsonAsync("/api/datasets",
            new CreateDatasetRequest("만료 시험", TaskType.Detection, ["good"]), Json))
            .Content.ReadFromJsonAsync<DatasetDto>(Json))!;
        var imageId = (await UploadAsync(eng, ("expire.png", MakePng(200, 200, SKColors.Green, 51)))).Results[0].Image.Id;
        await eng.PostAsJsonAsync($"/api/datasets/{dataset.Id}/images", new AddImagesRequest([imageId]), Json);
        await eng.PostAsync($"/api/datasets/{dataset.Id}/images/{imageId}/lock", null);

        // 브라우저가 그냥 닫힌 상황 — 잠금 시간이 지나면 다른 사람이 이어받는다
        _f.Clock.Advance(TimeSpan.FromMinutes(11));
        (await other.PostAsync($"/api/datasets/{dataset.Id}/images/{imageId}/lock", null))
            .StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Review_marks_labels_and_editing_them_clears_it()
    {
        var eng = await _f.EngineerAsync();
        var dataset = (await (await eng.PostAsJsonAsync("/api/datasets",
            new CreateDatasetRequest("검토 시험", TaskType.Detection, ["good"]), Json))
            .Content.ReadFromJsonAsync<DatasetDto>(Json))!;
        var imageId = (await UploadAsync(eng, ("review.png", MakePng(200, 200, SKColors.Yellow, 61)))).Results[0].Image.Id;
        await eng.PostAsJsonAsync($"/api/datasets/{dataset.Id}/images", new AddImagesRequest([imageId]), Json);

        // 라벨 없이 검토할 수는 없다
        (await eng.PostAsJsonAsync($"/api/datasets/{dataset.Id}/images/{imageId}/review", new ReviewRequest(true), Json))
            .StatusCode.Should().Be(HttpStatusCode.Conflict);

        await eng.PutAsJsonAsync($"/api/datasets/{dataset.Id}/images/{imageId}/labels",
            new SaveLabelsRequest([new AnnotationDto(AnnotationShape.Box, "good", 0.1, 0.1, 0.3, 0.3)], true), Json);
        var reviewed = (await (await eng.PostAsJsonAsync($"/api/datasets/{dataset.Id}/images/{imageId}/review",
            new ReviewRequest(true), Json)).Content.ReadFromJsonAsync<ImageLabelsDto>(Json))!;
        reviewed.Status.Should().Be(LabelStatus.Reviewed);
        reviewed.ReviewedBy.Should().Be("engineer");

        // 검토가 끝난 라벨을 고치면 검토 상태가 풀린다 — 바뀐 라벨은 다시 봐야 한다
        var edited = (await (await eng.PutAsJsonAsync($"/api/datasets/{dataset.Id}/images/{imageId}/labels",
            new SaveLabelsRequest([new AnnotationDto(AnnotationShape.Box, "good", 0.2, 0.2, 0.3, 0.3)], true), Json))
            .Content.ReadFromJsonAsync<ImageLabelsDto>(Json))!;
        edited.Status.Should().Be(LabelStatus.Labeled);
        edited.ReviewedBy.Should().BeNull();

        // 라벨러는 검토할 수 없다
        var labeler = await _f.ClientAsAsync("labeler-4", "Labeler");
        (await labeler.PostAsJsonAsync($"/api/datasets/{dataset.Id}/images/{imageId}/review", new ReviewRequest(true), Json))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Snapshot_needs_labels_and_can_require_review()
    {
        var eng = await _f.EngineerAsync();
        var dataset = (await (await eng.PostAsJsonAsync("/api/datasets",
            new CreateDatasetRequest("스냅샷 시험", TaskType.Detection, ["good"]), Json))
            .Content.ReadFromJsonAsync<DatasetDto>(Json))!;
        var imageId = (await UploadAsync(eng, ("snap.png", MakePng(200, 200, SKColors.Purple, 71)))).Results[0].Image.Id;
        await eng.PostAsJsonAsync($"/api/datasets/{dataset.Id}/images", new AddImagesRequest([imageId]), Json);

        var empty = await eng.PostAsJsonAsync($"/api/datasets/{dataset.Id}/versions", new CreateSnapshotRequest(), Json);
        empty.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ErrorAsync(empty))!.Message.Should().Contain("라벨");

        await eng.PutAsJsonAsync($"/api/datasets/{dataset.Id}/images/{imageId}/labels",
            new SaveLabelsRequest([new AnnotationDto(AnnotationShape.Box, "good", 0.1, 0.1, 0.3, 0.3)], true), Json);

        var reviewedOnly = await eng.PostAsJsonAsync($"/api/datasets/{dataset.Id}/versions",
            new CreateSnapshotRequest(ReviewedOnly: true), Json);
        reviewedOnly.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ErrorAsync(reviewedOnly))!.Message.Should().Contain("검토");

        (await eng.PostAsJsonAsync($"/api/datasets/{dataset.Id}/versions", new CreateSnapshotRequest(), Json))
            .StatusCode.Should().Be(HttpStatusCode.Created);
    }

    [Fact]
    public async Task Same_content_gives_the_same_manifest_hash()
    {
        var eng = await _f.EngineerAsync();
        var dataset = (await (await eng.PostAsJsonAsync("/api/datasets",
            new CreateDatasetRequest("재현성", TaskType.Detection, ["good"]), Json))
            .Content.ReadFromJsonAsync<DatasetDto>(Json))!;
        var imageId = (await UploadAsync(eng, ("repro.png", MakePng(200, 200, SKColors.Teal, 81)))).Results[0].Image.Id;
        await eng.PostAsJsonAsync($"/api/datasets/{dataset.Id}/images", new AddImagesRequest([imageId]), Json);
        await eng.PutAsJsonAsync($"/api/datasets/{dataset.Id}/images/{imageId}/labels",
            new SaveLabelsRequest([new AnnotationDto(AnnotationShape.Box, "good", 0.1, 0.1, 0.3, 0.3)], true), Json);

        var v1 = (await (await eng.PostAsJsonAsync($"/api/datasets/{dataset.Id}/versions", new CreateSnapshotRequest("a"), Json))
            .Content.ReadFromJsonAsync<DatasetVersionDto>(Json))!;
        var v2 = (await (await eng.PostAsJsonAsync($"/api/datasets/{dataset.Id}/versions", new CreateSnapshotRequest("b"), Json))
            .Content.ReadFromJsonAsync<DatasetVersionDto>(Json))!;

        // 이름만 다르고 내용이 같으면 매니페스트 해시도 같아야 한다 (재현성 레코드가 이 값을 남긴다)
        v2.ManifestHash.Should().Be(v1.ManifestHash);

        await eng.PutAsJsonAsync($"/api/datasets/{dataset.Id}/images/{imageId}/labels",
            new SaveLabelsRequest([new AnnotationDto(AnnotationShape.Box, "good", 0.5, 0.5, 0.2, 0.2)], true), Json);
        var v3 = (await (await eng.PostAsJsonAsync($"/api/datasets/{dataset.Id}/versions", new CreateSnapshotRequest("c"), Json))
            .Content.ReadFromJsonAsync<DatasetVersionDto>(Json))!;
        v3.ManifestHash.Should().NotBe(v1.ManifestHash, "라벨이 바뀌면 다른 스냅샷이다");
    }

    [Fact]
    public async Task Images_frozen_in_a_snapshot_cannot_be_deleted()
    {
        var eng = await _f.EngineerAsync();
        var dataset = (await (await eng.PostAsJsonAsync("/api/datasets",
            new CreateDatasetRequest("삭제 보호", TaskType.Detection, ["good"]), Json))
            .Content.ReadFromJsonAsync<DatasetDto>(Json))!;
        var imageId = (await UploadAsync(eng, ("frozen.png", MakePng(200, 200, SKColors.Orange, 91)))).Results[0].Image.Id;
        await eng.PostAsJsonAsync($"/api/datasets/{dataset.Id}/images", new AddImagesRequest([imageId]), Json);
        await eng.PutAsJsonAsync($"/api/datasets/{dataset.Id}/images/{imageId}/labels",
            new SaveLabelsRequest([new AnnotationDto(AnnotationShape.Box, "good", 0.1, 0.1, 0.3, 0.3)], true), Json);
        await eng.PostAsJsonAsync($"/api/datasets/{dataset.Id}/versions", new CreateSnapshotRequest(), Json);

        var res = await eng.PostAsJsonAsync("/api/images/delete", new DeleteImagesRequest([imageId]), Json);
        res.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await ErrorAsync(res))!.Message.Should().Contain("데이터셋 버전");
    }

    [Fact]
    public async Task Auto_split_is_stable_across_runs()
    {
        var eng = await _f.EngineerAsync();
        var dataset = (await (await eng.PostAsJsonAsync("/api/datasets",
            new CreateDatasetRequest("분할", TaskType.Detection, ["good"]), Json))
            .Content.ReadFromJsonAsync<DatasetDto>(Json))!;

        var files = Enumerable.Range(0, 10).Select(i => ($"s{i}.png", MakePng(64, 64, SKColors.Gray, 100 + i))).ToArray();
        var ids = (await UploadAsync(eng, files)).Results.Select(r => r.Image.Id).ToArray();
        await eng.PostAsJsonAsync($"/api/datasets/{dataset.Id}/images", new AddImagesRequest(ids), Json);

        async Task<List<(Guid Id, DatasetSplit Split)>> SplitsAsync()
        {
            var page = await eng.GetFromJsonAsync<ImagePageDto>($"/api/images?datasetId={dataset.Id}&take=100", Json);
            return page!.Items.Select(i => (i.Id, i.Split!.Value)).OrderBy(x => x.Id).ToList();
        }

        await eng.PostAsJsonAsync($"/api/datasets/{dataset.Id}/auto-split", new AutoSplitRequest(0.8, 0.2), Json);
        var first = await SplitsAsync();
        await eng.PostAsJsonAsync($"/api/datasets/{dataset.Id}/auto-split", new AutoSplitRequest(0.8, 0.2), Json);
        var second = await SplitsAsync();

        // 같은 비율로 다시 나누면 결과가 같아야 한다 — 무작위였다면 학습 결과가 흔들린다
        second.Should().Equal(first);
        first.Count(x => x.Split == DatasetSplit.Train).Should().Be(8);
        first.Count(x => x.Split == DatasetSplit.Val).Should().Be(2);
    }

    [Fact]
    public async Task Filters_narrow_the_pool_by_dataset_membership_and_label_state()
    {
        var eng = await _f.EngineerAsync();
        var dataset = (await (await eng.PostAsJsonAsync("/api/datasets",
            new CreateDatasetRequest("필터", TaskType.Detection, ["good"]), Json))
            .Content.ReadFromJsonAsync<DatasetDto>(Json))!;
        var ids = (await UploadAsync(eng,
            ("f1.png", MakePng(64, 64, SKColors.Aqua, 201)),
            ("f2.png", MakePng(64, 64, SKColors.Coral, 202)))).Results.Select(r => r.Image.Id).ToArray();

        await eng.PostAsJsonAsync($"/api/datasets/{dataset.Id}/images", new AddImagesRequest([ids[0]]), Json);

        var inSet = await eng.GetFromJsonAsync<ImagePageDto>($"/api/images?datasetId={dataset.Id}&inDataset=true", Json);
        inSet!.Items.Should().ContainSingle().Which.Id.Should().Be(ids[0]);

        var notInSet = await eng.GetFromJsonAsync<ImagePageDto>($"/api/images?datasetId={dataset.Id}&inDataset=false", Json);
        notInSet!.Items.Select(i => i.Id).Should().Contain(ids[1]).And.NotContain(ids[0]);

        // 아직 라벨이 없으니 미라벨로 잡힌다 (상태 레코드가 없어도)
        var unlabeled = await eng.GetFromJsonAsync<ImagePageDto>(
            $"/api/images?datasetId={dataset.Id}&labelStatus=unlabeled", Json);
        unlabeled!.Items.Select(i => i.Id).Should().Contain(ids[0]);

        await eng.PostAsJsonAsync("/api/images/tags", new TagImagesRequest([ids[1]], ["야간"]), Json);
        var tagged = await eng.GetFromJsonAsync<ImagePageDto>("/api/images?tag=야간", Json);
        tagged!.Items.Select(i => i.Id).Should().Contain(ids[1]).And.NotContain(ids[0]);
    }

    [Fact]
    public async Task Next_to_label_skips_finished_and_locked_images()
    {
        var eng = await _f.EngineerAsync();
        var dataset = (await (await eng.PostAsJsonAsync("/api/datasets",
            new CreateDatasetRequest("큐", TaskType.Detection, ["good"]), Json))
            .Content.ReadFromJsonAsync<DatasetDto>(Json))!;
        var ids = (await UploadAsync(eng,
            ("q1.png", MakePng(64, 64, SKColors.Silver, 301)),
            ("q2.png", MakePng(64, 64, SKColors.Sienna, 302)))).Results.Select(r => r.Image.Id).ToArray();
        await eng.PostAsJsonAsync($"/api/datasets/{dataset.Id}/images", new AddImagesRequest(ids), Json);

        var next = await eng.GetFromJsonAsync<NextImageDto>($"/api/datasets/{dataset.Id}/next-to-label", Json);
        next!.ImageId.Should().NotBeNull();

        // 둘 다 끝내면 더 줄 것이 없다
        foreach (var id in ids)
            await eng.PutAsJsonAsync($"/api/datasets/{dataset.Id}/images/{id}/labels",
                new SaveLabelsRequest([new AnnotationDto(AnnotationShape.Box, "good", 0.1, 0.1, 0.2, 0.2)], true), Json);

        (await eng.GetFromJsonAsync<NextImageDto>($"/api/datasets/{dataset.Id}/next-to-label", Json))!
            .ImageId.Should().BeNull();
    }

    [Fact]
    public async Task Viewer_can_look_but_not_label()
    {
        var viewer = await _f.ViewerAsync();
        (await viewer.GetAsync("/api/images")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await viewer.PostAsJsonAsync("/api/datasets",
            new CreateDatasetRequest("x", TaskType.Detection, ["a"]), Json)).StatusCode.Should().Be(HttpStatusCode.Forbidden);

        using var form = new MultipartFormDataContent();
        form.Add(new ByteArrayContent(MakePng(32, 32, SKColors.Black)), "files", "v.png");
        (await viewer.PostAsync("/api/images", form)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    private static async Task<Dictionary<string, string>> ReadZipAsync(HttpResponseMessage response)
    {
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        var bytes = await response.Content.ReadAsByteArrayAsync();
        using var archive = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);

        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var entry in archive.Entries)
        {
            // 이미지 본문은 필요 없고 존재만 확인하면 된다
            if (entry.FullName.EndsWith(".png") || entry.FullName.EndsWith(".jpg"))
            {
                result[entry.FullName] = "";
                continue;
            }
            using var reader = new StreamReader(entry.Open());
            result[entry.FullName] = await reader.ReadToEndAsync();
        }
        return result;
    }
}
