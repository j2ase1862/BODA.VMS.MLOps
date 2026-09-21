using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using BODA.VMS.MLOps.Contracts;
using BODA.VMS.MLOps.Contracts.Datasets;
using BODA.VMS.MLOps.Contracts.Models;
using BODA.VMS.MLOps.Contracts.Training;
using BODA.VMS.MLOps.Contracts.Workers;
using BODA.VMS.MLOps.Core.Domain;
using BODA.VMS.MLOps.Tests.TestAssets;
using FluentAssertions;
using SkiaSharp;
using static BODA.VMS.MLOps.Tests.Server.MlopsApiFactory;

namespace BODA.VMS.MLOps.Tests.Server;

/// <summary>
/// 지우는 길.
///
/// <para>여기서 지키는 것은 <b>순서</b>다. 굳은 것(데이터셋 버전·모델 버전)이 무른 것(사진·데이터셋·작업)을
/// 붙잡고, 붙잡힌 동안은 409 로 막힌다. 그래야 "학습을 돌린 그 판" 이 나중에도 재현된다.
/// 막는 곳을 하나라도 열면 그 사실은 몇 달 뒤 사고를 되짚을 때에야 드러난다.</para>
///
/// <para>파일도 함께 본다. 행만 지우고 파일을 두면 디스크가 조용히 찬다. 반대로 내용 주소 저장소에서
/// 남이 쓰는 파일을 지우면 멀쩡한 다른 버전이 404 가 된다.</para>
/// </summary>
public class DeletionApiTests : IClassFixture<MlopsApiFactory>
{
    private readonly MlopsApiFactory _f;
    public DeletionApiTests(MlopsApiFactory f) => _f = f;

    private string ModelFilePath(string sha256) =>
        Path.Combine(_f.StorageRoot, "models", sha256[..2], sha256 + ".onnx");

    private static async Task<ImageUploadBatchDto> UploadImagesAsync(HttpClient client, params (string Name, byte[] Bytes)[] files)
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

    /// <summary>
    /// 사진 → 데이터셋 → 버전 순으로 굳은 것을 풀어야 지워진다. 그리고 데이터셋을 지워도
    /// 사진은 풀에 남는다 — 라벨만 사라진다. 이 구분이 화면 확인 문구의 근거다.
    /// </summary>
    [Fact]
    public async Task Dataset_chain_unwinds_in_order_and_photos_outlive_the_dataset()
    {
        var eng = await _f.EngineerAsync();
        var dataset = (await (await eng.PostAsJsonAsync("/api/datasets",
            new CreateDatasetRequest("지울 데이터셋", TaskType.Detection, ["good", "defect"]), Json))
            .Content.ReadFromJsonAsync<DatasetDto>(Json))!;

        var upload = await UploadImagesAsync(eng,
            ("del1.png", DataManagementApiTests.MakePng(320, 240, SKColors.White, 51)),
            ("del2.png", DataManagementApiTests.MakePng(320, 240, SKColors.Black, 52)));
        var ids = upload.Results.Select(r => r.Image.Id).ToArray();
        (await eng.PostAsJsonAsync($"/api/datasets/{dataset.Id}/images", new AddImagesRequest(ids, DatasetSplit.Train), Json))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        foreach (var id in ids)
            (await eng.PutAsJsonAsync($"/api/datasets/{dataset.Id}/images/{id}/labels",
                new SaveLabelsRequest([new AnnotationDto(AnnotationShape.Box, "defect", 0.2, 0.2, 0.3, 0.3)], true), Json))
                .StatusCode.Should().Be(HttpStatusCode.OK);

        var version = (await (await eng.PostAsJsonAsync($"/api/datasets/{dataset.Id}/versions",
            new CreateSnapshotRequest("굳은 판"), Json)).Content.ReadFromJsonAsync<DatasetVersionDto>(Json))!;

        // 버전이 있는 동안은 데이터셋도 사진도 지울 수 없다
        var blocked = await eng.DeleteAsync($"/api/datasets/{dataset.Id}");
        blocked.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await ErrorAsync(blocked))!.Message.Should().Contain("버전");

        var blockedImages = await eng.PostAsJsonAsync("/api/images/delete", new DeleteImagesRequest(ids), Json);
        blockedImages.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await ErrorAsync(blockedImages))!.Message.Should().Contain("데이터셋 버전");

        // 버전 → 데이터셋 순서로 풀린다
        (await eng.DeleteAsync($"/api/dataset-versions/{version.Id}")).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await eng.GetAsync($"/api/dataset-versions/{version.Id}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await eng.DeleteAsync($"/api/datasets/{dataset.Id}")).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await eng.GetAsync($"/api/datasets/{dataset.Id}")).StatusCode.Should().Be(HttpStatusCode.NotFound);

        // 사진은 살아 있다 — 데이터셋 삭제는 라벨만 거둬 간다
        foreach (var id in ids)
            (await eng.GetAsync($"/api/images/{id}")).StatusCode.Should().Be(HttpStatusCode.OK);

        // 이제 풀에서도 지운다. 파일까지 간다.
        var image = await eng.GetFromJsonAsync<ImageDto>($"/api/images/{ids[0]}", Json);
        var deleted = await eng.PostAsJsonAsync("/api/images/delete", new DeleteImagesRequest(ids), Json);
        deleted.StatusCode.Should().Be(HttpStatusCode.OK);
        (await deleted.Content.ReadFromJsonAsync<JsonElement>(Json)).GetProperty("deleted").GetInt32().Should().Be(2);
        (await eng.GetAsync($"/api/images/{ids[0]}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await eng.GetAsync(image!.ThumbnailUrl)).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    /// <summary>
    /// 끝난 작업만 지워진다. 그리고 그 작업이 낸 모델 버전이 있으면 버전을 먼저 지워야 한다 —
    /// 버전의 출처(어느 판, 어떤 설정)는 작업 행에만 있다.
    /// </summary>
    [Fact]
    public async Task Finished_job_deletes_with_its_logs_and_artifacts_but_not_while_a_version_points_at_it()
    {
        var admin = await _f.AdminAsync();
        var eng = await _f.EngineerAsync();
        await EnsurePretrainedAsync(admin);

        var model = await CreateModelAsync(eng, "m-job-delete");
        var ds = await UploadDatasetAsync(eng, "ds-job-delete", zip: MakeYoloDatasetZip(7));
        var job = (await (await eng.PostAsJsonAsync("/api/training-jobs",
            new CreateTrainingJobRequest(ds.Id, model.Id, TrainingScript.TrainDfine, PretrainedRef: SharedPretrainedRef,
                Hyperparams: JsonDocument.Parse("""{"epochs": 1}""").RootElement.Clone()), Json))
            .Content.ReadFromJsonAsync<TrainingJobDto>(Json))!;

        // 큐에 있는 동안은 지울 수 없다 — 워커가 받아 갈 수 있는 상태다
        var queued = await eng.DeleteAsync($"/api/training-jobs/{job.Id}");
        queued.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await ErrorAsync(queued))!.Code.Should().Be(ErrorCodes.InvalidJobTransition);

        // 워커가 받아 아티팩트를 올리면 Candidate 가 생긴다
        var created = await _f.CreateWorkerAsync(admin, "gpu-delete");
        var w = _f.WorkerClient(created.Token);
        (await w.PostAsJsonAsync("/api/workers/register",
            new RegisterWorkerRequest("gpu-delete", "GPU-PC", OkCapabilities(), Enum.GetValues<TaskType>(), "0.1.0"), Json))
            .EnsureSuccessStatusCode();
        var assignment = (await (await w.GetAsync("/api/training-jobs/next?wait=0")).Content.ReadFromJsonAsync<JobAssignment>(Json))!;
        assignment.Job.Id.Should().Be(job.Id);
        (await w.PostAsync($"/api/training-jobs/{job.Id}/ack", null)).EnsureSuccessStatusCode();
        (await w.PatchAsJsonAsync($"/api/training-jobs/{job.Id}/progress",
            new ProgressReport(TrainingJobState.Running, 50, 1, 1, 0.5, 0.8,
                [new LogChunkDto(JobLogLevel.Stdout, "[EPOCH] 1/1", DateTime.UtcNow)]), Json)).EnsureSuccessStatusCode();

        using var form = new MultipartFormDataContent();
        form.Add(new ByteArrayContent(OnnxStubs.Bytes(OnnxStubs.DeployNoMeta)), "onnx", "best.onnx");
        form.Add(new StringContent("""{"map50": 0.5}""", Encoding.UTF8, "application/json"), "metrics", "metrics.json");
        form.Add(new StringContent("""{"epochs": 1, "names": {"0": "good", "1": "defect"}, "imgsz": 640}""",
            Encoding.UTF8, "application/json"), "train_info", "vms_train_info.json");
        var artifacts = (await (await w.PostAsync($"/api/training-jobs/{job.Id}/artifacts", form))
            .Content.ReadFromJsonAsync<ArtifactsResponse>(Json))!;
        artifacts.ModelVersionId.Should().NotBeNull();
        (await w.PostAsJsonAsync($"/api/training-jobs/{job.Id}/finish", new FinishJobRequest(TrainingJobState.Succeeded), Json))
            .EnsureSuccessStatusCode();

        var jobDir = Path.Combine(_f.StorageRoot, "jobs", job.Id.ToString("N"));
        Directory.GetFiles(jobDir).Should().NotBeEmpty();

        // 끝났지만 결과 버전이 이 작업을 가리키는 동안은 막힌다
        var held = await eng.DeleteAsync($"/api/training-jobs/{job.Id}");
        held.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await ErrorAsync(held))!.Message.Should().Contain("모델 버전");

        // 버전을 지우면 작업도 지워지고, 로그와 아티팩트가 함께 간다
        (await eng.DeleteAsync($"/api/model-versions/{artifacts.ModelVersionId}")).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await eng.DeleteAsync($"/api/training-jobs/{job.Id}")).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await eng.GetAsync($"/api/training-jobs/{job.Id}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await eng.GetAsync($"/api/training-jobs/{job.Id}/logs")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        Directory.GetFiles(jobDir).Should().BeEmpty("아티팩트 파일도 함께 간다");
    }

    /// <summary>
    /// 라인이 받아 갈 수 있는 버전(Staging·Production)은 지워지지 않고, 한 번 나갔던 것(Retired)은 Admin 만 지운다.
    /// 레시피가 묶고 있어도 막는다.
    /// </summary>
    [Fact]
    public async Task Version_delete_respects_stage_and_active_bindings()
    {
        var eng = await _f.EngineerAsync();
        var admin = await _f.AdminAsync();
        var model = await CreateModelAsync(eng, "m-stage-delete");
        var version = (await (await UploadVersionAsync(eng, model.Id, OnnxStubs.Bytes(OnnxStubs.DeployWithMeta)))
            .Content.ReadFromJsonAsync<ModelVersionDto>(Json))!;

        // 레시피가 묶고 있으면 Candidate 라도 막는다
        (await eng.PutAsJsonAsync("/api/recipes/R-DEL/tools/T-1/model",
            new SetBindingRequest(BindingMode.Pinned, version.Id), Json)).EnsureSuccessStatusCode();
        var bound = await eng.DeleteAsync($"/api/model-versions/{version.Id}");
        bound.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await ErrorAsync(bound))!.Message.Should().Contain("묶여");

        // 바인딩을 다른 버전으로 옮기면 풀린다
        var other = (await (await UploadVersionAsync(eng, model.Id, OnnxStubs.Bytes(OnnxStubs.DeployNoMeta),
            new VersionUploadMeta(Classes: ["good", "defect"], InputSize: 640)))
            .Content.ReadFromJsonAsync<ModelVersionDto>(Json))!;
        (await eng.PutAsJsonAsync("/api/recipes/R-DEL/tools/T-1/model",
            new SetBindingRequest(BindingMode.Pinned, other.Id), Json)).EnsureSuccessStatusCode();

        // Staging·Production 은 스테이지를 내리기 전에는 못 지운다
        (await eng.PostAsJsonAsync($"/api/model-versions/{version.Id}/promote",
            new PromoteRequest(ModelStage.Staging), Json)).EnsureSuccessStatusCode();
        var staging = await eng.DeleteAsync($"/api/model-versions/{version.Id}");
        staging.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await ErrorAsync(staging))!.Code.Should().Be(ErrorCodes.InvalidStageTransition);

        (await admin.PostAsJsonAsync($"/api/model-versions/{version.Id}/promote",
            new PromoteRequest(ModelStage.Production), Json)).EnsureSuccessStatusCode();
        (await admin.DeleteAsync($"/api/model-versions/{version.Id}")).StatusCode.Should().Be(HttpStatusCode.Conflict);

        // Retired 는 Admin 만
        (await admin.PostAsJsonAsync($"/api/model-versions/{version.Id}/promote",
            new PromoteRequest(ModelStage.Retired, "라인에서 내림"), Json)).EnsureSuccessStatusCode();
        (await eng.DeleteAsync($"/api/model-versions/{version.Id}")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await admin.DeleteAsync($"/api/model-versions/{version.Id}")).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await eng.GetAsync($"/api/model-versions/{version.Id}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    /// <summary>
    /// 아티팩트 경로는 내용 해시라, 같은 ONNX 를 다른 계열에도 올리면 두 행이 한 파일을 나눠 쓴다.
    /// 한쪽을 지운다고 파일을 가져가면 남은 쪽이 404 가 된다.
    /// </summary>
    [Fact]
    public async Task Shared_artifact_file_survives_until_the_last_version_goes()
    {
        var eng = await _f.EngineerAsync();
        var first = await CreateModelAsync(eng, "m-shared-a");
        var second = await CreateModelAsync(eng, "m-shared-b");
        // 이 시험만 쓰는 스텁이어야 "이 파일을 쓰는 마지막 버전" 을 실제로 셀 수 있다 —
        // 다른 시험이 같은 바이트를 올려 두면 파일이 남는 것이 맞는 동작이라 시험이 뜻을 잃는다.
        var onnx = OnnxStubs.Bytes(OnnxStubs.RawHf);

        var v1 = (await (await UploadVersionAsync(eng, first.Id, onnx)).Content.ReadFromJsonAsync<ModelVersionDto>(Json))!;
        var v2 = (await (await UploadVersionAsync(eng, second.Id, onnx)).Content.ReadFromJsonAsync<ModelVersionDto>(Json))!;
        v1.Id.Should().NotBe(v2.Id);
        v1.Sha256.Should().Be(v2.Sha256);

        (await eng.DeleteAsync($"/api/model-versions/{v1.Id}")).StatusCode.Should().Be(HttpStatusCode.NoContent);
        File.Exists(ModelFilePath(v1.Sha256)).Should().BeTrue("다른 계열의 버전이 같은 파일을 쓰고 있다");
        (await eng.GetAsync($"/api/model-versions/{v2.Id}/artifact")).StatusCode.Should().Be(HttpStatusCode.OK);

        (await eng.DeleteAsync($"/api/model-versions/{v2.Id}")).StatusCode.Should().Be(HttpStatusCode.NoContent);
        File.Exists(ModelFilePath(v2.Sha256)).Should().BeFalse();
    }

    /// <summary>계열 삭제는 버전·작업·바인딩이 하나도 없을 때만. 쓰던 계열을 치우는 것은 보관이다.</summary>
    [Fact]
    public async Task Model_family_deletes_only_when_nothing_hangs_off_it()
    {
        var eng = await _f.EngineerAsync();
        var model = await CreateModelAsync(eng, "m-family-delete");
        var version = (await (await UploadVersionAsync(eng, model.Id, OnnxStubs.Bytes(OnnxStubs.DeployWithMeta)))
            .Content.ReadFromJsonAsync<ModelVersionDto>(Json))!;

        var blocked = await eng.DeleteAsync($"/api/models/{model.Id}");
        blocked.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await ErrorAsync(blocked))!.Message.Should().Contain("버전");

        (await eng.DeleteAsync($"/api/model-versions/{version.Id}")).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await eng.DeleteAsync($"/api/models/{model.Id}")).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await eng.GetAsync($"/api/models/{model.Id}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    /// <summary>보는 사람은 지우지 못한다 — 삭제는 전부 Engineer 이상이다.</summary>
    [Fact]
    public async Task Viewer_cannot_delete_anything()
    {
        var eng = await _f.EngineerAsync();
        var viewer = await _f.ViewerAsync();
        var model = await CreateModelAsync(eng, "m-viewer-delete");
        var version = (await (await UploadVersionAsync(eng, model.Id, OnnxStubs.Bytes(OnnxStubs.DeployWithMeta)))
            .Content.ReadFromJsonAsync<ModelVersionDto>(Json))!;
        var dataset = (await (await eng.PostAsJsonAsync("/api/datasets",
            new CreateDatasetRequest("보기 전용", TaskType.Detection, ["good", "defect"]), Json))
            .Content.ReadFromJsonAsync<DatasetDto>(Json))!;

        foreach (var path in new[] { $"/api/models/{model.Id}", $"/api/model-versions/{version.Id}", $"/api/datasets/{dataset.Id}" })
            (await viewer.DeleteAsync(path)).StatusCode.Should().Be(HttpStatusCode.Forbidden, path);
        (await viewer.PostAsJsonAsync("/api/images/delete", new DeleteImagesRequest([Guid.NewGuid()]), Json))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }
}
