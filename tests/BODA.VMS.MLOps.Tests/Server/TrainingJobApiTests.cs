using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using BODA.VMS.MLOps.Contracts;
using BODA.VMS.MLOps.Contracts.Models;
using BODA.VMS.MLOps.Contracts.Training;
using BODA.VMS.MLOps.Contracts.Workers;
using BODA.VMS.MLOps.Core.Domain;
using BODA.VMS.MLOps.Server.Services;
using BODA.VMS.MLOps.Tests.TestAssets;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using static BODA.VMS.MLOps.Tests.Server.MlopsApiFactory;

namespace BODA.VMS.MLOps.Tests.Server;

/// <summary>Phase 3 §11 서버 단위: 워커 등록/하트비트, 배정 규칙, 상태 머신, 아티팩트→ModelVersion, 취소, ack 타임아웃·WorkerLost 재큐, 인자 화이트리스트</summary>
public class TrainingJobApiTests : IClassFixture<MlopsApiFactory>
{
    private readonly MlopsApiFactory _f;
    public TrainingJobApiTests(MlopsApiFactory f) => _f = f;

    private static JsonElement Hp(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private async Task<(HttpClient Worker, Guid WorkerId)> OnlineWorkerAsync(string name, TaskType[]? taskTypes = null, double diskGB = 500)
    {
        var admin = await _f.AdminAsync();
        var created = await _f.CreateWorkerAsync(admin, name, taskTypes);
        created.Token.Should().StartWith("wk_");
        var w = _f.WorkerClient(created.Token);
        var reg = await w.PostAsJsonAsync("/api/workers/register",
            new RegisterWorkerRequest(name, "GPU-PC", OkCapabilities(diskGB), taskTypes ?? Enum.GetValues<TaskType>(), "0.1.0"), Json);
        reg.EnsureSuccessStatusCode();
        var body = (await reg.Content.ReadFromJsonAsync<RegisterWorkerResponse>(Json))!;
        body.Status.Should().Be(WorkerStatus.Online);
        body.ScriptsManifest.Should().ContainKey("train_dfine.py");
        return (w, created.WorkerId);
    }

    private async Task<(TrainingJobDto Job, ModelDto Model)> QueuedJobAsync(HttpClient eng, string modelName, string hp = """{"epochs": 2, "batch_size": 8}""", int priority = 0)
    {
        var model = await CreateModelAsync(eng, modelName);
        var ds = await UploadDatasetAsync(eng, "ds-" + modelName, zip: MakeYoloDatasetZip(modelName.Length % 20));
        var res = await eng.PostAsJsonAsync("/api/training-jobs", new CreateTrainingJobRequest(ds.Id, model.Id, TrainingScript.TrainDfine, Hyperparams: Hp(hp), Priority: priority), Json);
        res.StatusCode.Should().Be(HttpStatusCode.Created);
        return ((await res.Content.ReadFromJsonAsync<TrainingJobDto>(Json))!, model);
    }

    [Fact]
    public async Task Worker_token_is_service_account_and_diagnostics_failure_disables()
    {
        var admin = await _f.AdminAsync();
        var created = await _f.CreateWorkerAsync(admin, "diag-fail");
        var w = _f.WorkerClient(created.Token);

        // 워커 토큰은 레시피·모델 생성 같은 사용자 API 에 접근 불가
        (await w.PostAsJsonAsync("/api/models", new CreateModelRequest("x", TaskType.Detection, ["a"]), Json)).StatusCode.Should().Be(HttpStatusCode.Forbidden);

        var caps = OkCapabilities() with { CudaAvailable = false, DiagnosticFailures = ["torch.cuda.is_available() == False"] };
        var reg = await (await w.PostAsJsonAsync("/api/workers/register", new RegisterWorkerRequest("diag-fail", "PC", caps, [TaskType.Detection], "0.1.0"), Json))
            .Content.ReadFromJsonAsync<RegisterWorkerResponse>(Json);
        reg!.Status.Should().Be(WorkerStatus.Disabled);
        reg.DisabledReason.Should().Contain("진단 실패");

        (await w.GetAsync("/api/training-jobs/next?wait=0")).StatusCode.Should().Be(HttpStatusCode.Forbidden);

        // 잘못된 토큰은 401
        (await _f.WorkerClient("wk_bogus").GetAsync("/api/training-jobs/next?wait=0")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        // 프로토콜 버전 불일치 → Disabled(업데이트 필요)
        var w2 = _f.CreateClient();
        w2.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", created.Token);
        w2.DefaultRequestHeaders.Add(MlopsJson.ProtocolHeader, "99");
        var reg2 = await (await w2.PostAsJsonAsync("/api/workers/register", new RegisterWorkerRequest("diag-fail", "PC", OkCapabilities(), [TaskType.Detection], "0.1.0"), Json))
            .Content.ReadFromJsonAsync<RegisterWorkerResponse>(Json);
        reg2!.Status.Should().Be(WorkerStatus.Disabled);
        reg2.DisabledReason.Should().Contain("업데이트");
    }

    [Fact]
    public async Task Create_job_validates_hyperparams_task_type_and_duplicates()
    {
        var eng = await _f.EngineerAsync();
        var model = await CreateModelAsync(eng, "m-create");
        var ds = await UploadDatasetAsync(eng, "ds-create");

        var bad = await eng.PostAsJsonAsync("/api/training-jobs", new CreateTrainingJobRequest(ds.Id, model.Id, TrainingScript.TrainDfine, Hyperparams: Hp("""{"epochs": 2, "evil": "x"}""")), Json);
        bad.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var err = await ErrorAsync(bad);
        err!.Code.Should().Be(ErrorCodes.InvalidHyperparams);
        err.Details.Should().ContainSingle(d => d.Contains("evil"));

        var cls = await CreateModelAsync(eng, "m-create-cls", TaskType.Classification);
        var mismatch = await eng.PostAsJsonAsync("/api/training-jobs", new CreateTrainingJobRequest(ds.Id, cls.Id, TrainingScript.TrainDfine), Json);
        (await ErrorAsync(mismatch))!.Code.Should().Be(ErrorCodes.TaskTypeMismatch);

        var yoloNoLicense = await eng.PostAsJsonAsync("/api/training-jobs", new CreateTrainingJobRequest(ds.Id, model.Id, TrainingScript.TrainYolo), Json);
        (await ErrorAsync(yoloNoLicense))!.Code.Should().Be(ErrorCodes.LicenseRequired);

        var missingPretrained = await eng.PostAsJsonAsync("/api/training-jobs", new CreateTrainingJobRequest(ds.Id, model.Id, TrainingScript.TrainDfine, PretrainedRef: "nope"), Json);
        missingPretrained.StatusCode.Should().Be(HttpStatusCode.NotFound);

        // Viewer 는 작업 생성 불가
        (await (await _f.ViewerAsync()).PostAsJsonAsync("/api/training-jobs", new CreateTrainingJobRequest(ds.Id, model.Id, TrainingScript.TrainDfine), Json))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Full_worker_protocol_assign_ack_progress_artifacts_finish_creates_candidate()
    {
        var eng = await _f.EngineerAsync();
        var (job, model) = await QueuedJobAsync(eng, "m-full", """{"epochs": 3, "batch_size": 8, "lr": 0.00025}""", priority: 5);
        var (w, workerId) = await OnlineWorkerAsync("gpu-full");

        // 배정 (우선순위 높은 이 작업이 먼저)
        var next = await w.GetAsync("/api/training-jobs/next?wait=0");
        next.StatusCode.Should().Be(HttpStatusCode.OK);
        var a = (await next.Content.ReadFromJsonAsync<JobAssignment>(Json))!;
        a.Job.Id.Should().Be(job.Id);
        a.Job.State.Should().Be(TrainingJobState.Assigned);
        a.Job.WorkerId.Should().Be(workerId);
        a.Job.Attempt.Should().Be(1);
        a.ScriptName.Should().Be("train_dfine.py");
        a.ScriptSha256.Should().HaveLength(64);
        a.DatasetExportUrl.Should().Contain("format=yolo");
        a.Job.Hyperparams["lr"].Should().Be("0.00025");

        // 재접속 시 같은 배정을 다시 준다 (멱등)
        var again = (await (await w.GetAsync("/api/training-jobs/next?wait=0")).Content.ReadFromJsonAsync<JobAssignment>(Json))!;
        again.Job.Id.Should().Be(job.Id);

        // 스크립트 본문·데이터셋 export 접근 (워커 토큰)
        var script = await w.GetAsync(a.ScriptUrl);
        script.StatusCode.Should().Be(HttpStatusCode.OK);
        (await script.Content.ReadAsStringAsync()).Should().Contain("fake trainer");
        var export = await w.GetAsync(a.DatasetExportUrl);
        export.StatusCode.Should().Be(HttpStatusCode.OK);
        export.Headers.ETag!.Tag.Should().Be($"\"{a.DatasetManifestHash}\"");
        export.Headers.GetValues("X-Content-Sha256").Single().Should().Be(a.DatasetManifestHash);

        // ack → Preparing
        (await (await w.PostAsync($"/api/training-jobs/{job.Id}/ack", null)).Content.ReadFromJsonAsync<TrainingJobDto>(Json))!.State.Should().Be(TrainingJobState.Preparing);

        // 다른 워커는 이 작업에 보고 불가
        var (other, _) = await OnlineWorkerAsync("gpu-other");
        (await other.PostAsync($"/api/training-jobs/{job.Id}/ack", null)).StatusCode.Should().Be(HttpStatusCode.Forbidden);

        // progress: Running + 로그
        var p1 = await w.PatchAsJsonAsync($"/api/training-jobs/{job.Id}/progress",
            new ProgressReport(TrainingJobState.Running, 33.3, 1, 3, 0.9, 0.6, [new LogChunkDto(JobLogLevel.Stdout, "[EPOCH] 1/3", DateTime.UtcNow)]), Json);
        p1.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await p1.Content.ReadFromJsonAsync<JsonElement>(Json);
        body.GetProperty("cancelRequested").GetBoolean().Should().BeFalse();
        body.GetProperty("job").GetProperty("state").GetString().Should().Be("running");

        // 역방향 전이 거부
        var back = await w.PatchAsJsonAsync($"/api/training-jobs/{job.Id}/progress", new ProgressReport(TrainingJobState.Preparing, 40, 1, 3), Json);
        back.StatusCode.Should().Be(HttpStatusCode.Conflict);

        // Succeeded 는 아티팩트 없이는 불가
        var early = await w.PostAsJsonAsync($"/api/training-jobs/{job.Id}/finish", new FinishJobRequest(TrainingJobState.Succeeded), Json);
        early.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        // 아티팩트 업로드 → ModelVersion(Candidate, Source=TrainingJob)
        using var form = new MultipartFormDataContent();
        form.Add(new ByteArrayContent(OnnxStubs.Bytes(OnnxStubs.DeployNoMeta)), "onnx", "best.onnx");
        form.Add(new StringContent("""{"map50": 0.91, "val_loss": 0.12}""", Encoding.UTF8, "application/json"), "metrics", "metrics.json");
        form.Add(new StringContent("""{"epochs": 3, "names": {"0": "good", "1": "defect"}, "imgsz": 640, "pretrained": "dfine-s"}""", Encoding.UTF8, "application/json"), "train_info", "vms_train_info.json");
        form.Add(new StringContent("[EPOCH] 1/3\n[DONE]\n"), "log", "train.log");
        var art = await w.PostAsync($"/api/training-jobs/{job.Id}/artifacts", form);
        art.StatusCode.Should().Be(HttpStatusCode.OK, await art.Content.ReadAsStringAsync());
        var ar = (await art.Content.ReadFromJsonAsync<ArtifactsResponse>(Json))!;
        ar.ModelVersionId.Should().NotBeNull();
        ar.Artifacts.Should().HaveCount(4);

        var version = await eng.GetFromJsonAsync<ModelVersionDto>($"/api/model-versions/{ar.ModelVersionId}", Json);
        version!.ModelId.Should().Be(model.Id);
        version.Source.Should().Be(VersionSource.TrainingJob);
        version.TrainingJobId.Should().Be(job.Id);
        version.Format.Should().Be(ModelFormat.DFine);
        version.Classes.Should().Equal("good", "defect");
        version.Metrics["map50"].Should().Be(0.91);
        version.Metrics["epochs"].Should().Be(3);

        // finish
        var repro = new ReproducibilityRecord(a.DatasetManifestHash, a.ScriptSha256, new() { ["torch"] = "2.6.0" }, 0, "gpu-full", "RTX 4090", "3.12", a.Job.Hyperparams);
        var fin = (await (await w.PostAsJsonAsync($"/api/training-jobs/{job.Id}/finish", new FinishJobRequest(TrainingJobState.Succeeded, Reproducibility: repro), Json))
            .Content.ReadFromJsonAsync<TrainingJobDto>(Json))!;
        fin.State.Should().Be(TrainingJobState.Succeeded);
        fin.Progress.Should().Be(100);
        fin.ResultModelVersionId.Should().Be(ar.ModelVersionId);
        fin.Reproducibility!.ScriptSha256.Should().Be(a.ScriptSha256);

        // 워커는 다시 Online, 로그 조회 가능
        (await eng.GetFromJsonAsync<WorkerDto>($"/api/workers/{workerId}", Json))!.Status.Should().Be(WorkerStatus.Online);
        var logs = await eng.GetFromJsonAsync<JobLogPage>($"/api/training-jobs/{job.Id}/logs", Json);
        logs!.Lines.Should().ContainSingle(l => l.Text == "[EPOCH] 1/3");

        // 같은 설정 재제출 → 409 DuplicateJob, force 면 허용; retry 엔드포인트도 새 작업
        var dup = await eng.PostAsJsonAsync("/api/training-jobs", new CreateTrainingJobRequest(job.DatasetVersionId, model.Id, TrainingScript.TrainDfine, Hyperparams: Hp("""{"epochs": 3, "batch_size": 8, "lr": 0.00025}"""), Priority: 5), Json);
        dup.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await ErrorAsync(dup))!.Code.Should().Be(ErrorCodes.DuplicateJob);
        (await eng.PostAsync($"/api/training-jobs/{job.Id}/retry", null)).StatusCode.Should().Be(HttpStatusCode.Created);
    }

    [Fact]
    public async Task Assignment_respects_task_type_and_disk()
    {
        var eng = await _f.EngineerAsync();
        var (job, _) = await QueuedJobAsync(eng, "m-assign-rules");

        var (clsOnly, _) = await OnlineWorkerAsync("gpu-cls-only", [TaskType.Classification]);
        (await clsOnly.GetAsync("/api/training-jobs/next?wait=0")).StatusCode.Should().Be(HttpStatusCode.NoContent);

        var (noDisk, _) = await OnlineWorkerAsync("gpu-nodisk", [TaskType.Detection], diskGB: 0.0000001);
        (await noDisk.GetAsync("/api/training-jobs/next?wait=0")).StatusCode.Should().Be(HttpStatusCode.NoContent);

        var (ok, _) = await OnlineWorkerAsync("gpu-ok", [TaskType.Detection]);
        var a = await (await ok.GetAsync("/api/training-jobs/next?wait=0")).Content.ReadFromJsonAsync<JobAssignment>(Json);
        a!.Job.Id.Should().Be(job.Id);
        await ok.PostAsJsonAsync($"/api/training-jobs/{job.Id}/finish", new FinishJobRequest(TrainingJobState.Failed, "cleanup"), Json);
    }

    [Fact]
    public async Task Cancel_queued_directly_and_running_via_heartbeat()
    {
        var eng = await _f.EngineerAsync();
        var admin = await _f.AdminAsync();
        var (queued, _) = await QueuedJobAsync(eng, "m-cancel-q");
        var c = (await (await eng.PostAsJsonAsync($"/api/training-jobs/{queued.Id}/cancel", new CancelJobRequest("mind changed"), Json)).Content.ReadFromJsonAsync<TrainingJobDto>(Json))!;
        c.State.Should().Be(TrainingJobState.Cancelled);

        var (running, _) = await QueuedJobAsync(eng, "m-cancel-r", priority: 9);
        var (w, workerId) = await OnlineWorkerAsync("gpu-cancel");
        var a = await (await w.GetAsync("/api/training-jobs/next?wait=0")).Content.ReadFromJsonAsync<JobAssignment>(Json);
        a!.Job.Id.Should().Be(running.Id);
        await w.PostAsync($"/api/training-jobs/{running.Id}/ack", null);
        await w.PatchAsJsonAsync($"/api/training-jobs/{running.Id}/progress", new ProgressReport(TrainingJobState.Running, 10, 1, 5), Json);

        // 다른 사람(other) 의 작업 취소는 Admin 만
        var other = await _f.ClientAsAsync("someone-else", "Engineer");
        (await other.PostAsJsonAsync($"/api/training-jobs/{running.Id}/cancel", new CancelJobRequest(), Json)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        var req = (await (await admin.PostAsJsonAsync($"/api/training-jobs/{running.Id}/cancel", new CancelJobRequest("admin"), Json)).Content.ReadFromJsonAsync<TrainingJobDto>(Json))!;
        req.State.Should().Be(TrainingJobState.Running);
        req.CancelRequested.Should().BeTrue();

        // 하트비트가 취소를 전달
        var hb = await (await w.PostAsJsonAsync($"/api/workers/{workerId}/heartbeat", new HeartbeatRequest(WorkerStatus.Busy, running.Id, 400, 20000), Json))
            .Content.ReadFromJsonAsync<HeartbeatResponse>(Json);
        hb!.CancelRequested.Should().Be(running.Id);

        // progress 응답에도 cancelRequested
        var p = await (await w.PatchAsJsonAsync($"/api/training-jobs/{running.Id}/progress", new ProgressReport(null, 20, 2, 5), Json)).Content.ReadFromJsonAsync<JsonElement>(Json);
        p.GetProperty("cancelRequested").GetBoolean().Should().BeTrue();

        var fin = (await (await w.PostAsJsonAsync($"/api/training-jobs/{running.Id}/finish", new FinishJobRequest(TrainingJobState.Cancelled, "killed"), Json)).Content.ReadFromJsonAsync<TrainingJobDto>(Json))!;
        fin.State.Should().Be(TrainingJobState.Cancelled);
        (await eng.PostAsJsonAsync($"/api/training-jobs/{running.Id}/cancel", new CancelJobRequest(), Json)).StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Supervisor_requeues_on_ack_timeout_and_worker_lost_then_fails_after_max_attempts()
    {
        var eng = await _f.EngineerAsync();
        var (job, _) = await QueuedJobAsync(eng, "m-supervise", priority: 20);
        var (w, workerId) = await OnlineWorkerAsync("gpu-lost");

        var a = await (await w.GetAsync("/api/training-jobs/next?wait=0")).Content.ReadFromJsonAsync<JobAssignment>(Json);
        a!.Job.Id.Should().Be(job.Id);
        a.Job.Attempt.Should().Be(1);

        // ack 없이 61초 경과 → 재큐 (Attempt 1 < Max 2)
        _f.Clock.Advance(TimeSpan.FromSeconds(61));
        await SuperviseAsync();
        var j = await eng.GetFromJsonAsync<TrainingJobDto>($"/api/training-jobs/{job.Id}", Json);
        j!.State.Should().Be(TrainingJobState.Queued);
        j.WorkerId.Should().BeNull();
        j.FailureKind.Should().Be(FailureKinds.AckTimeout);

        // 하트비트로 워커를 살린 뒤 다시 배정·ack·Running → 하트비트 소실 → WorkerLost, Attempt 2 == Max → Failed
        await w.PostAsJsonAsync($"/api/workers/{workerId}/heartbeat", new HeartbeatRequest(WorkerStatus.Online, null, 400, 20000), Json);
        a = await (await w.GetAsync("/api/training-jobs/next?wait=0")).Content.ReadFromJsonAsync<JobAssignment>(Json);
        a!.Job.Attempt.Should().Be(2);
        await w.PostAsync($"/api/training-jobs/{job.Id}/ack", null);
        await w.PatchAsJsonAsync($"/api/training-jobs/{job.Id}/progress", new ProgressReport(TrainingJobState.Running, 5, 1, 3), Json);

        _f.Clock.Advance(TimeSpan.FromSeconds(91));
        await SuperviseAsync();
        j = await eng.GetFromJsonAsync<TrainingJobDto>($"/api/training-jobs/{job.Id}", Json);
        j!.State.Should().Be(TrainingJobState.Failed);
        j.FailureKind.Should().Be(FailureKinds.WorkerLost);
        (await eng.GetFromJsonAsync<WorkerDto>($"/api/workers/{workerId}", Json))!.Status.Should().Be(WorkerStatus.Offline);

        // 사라진 워커가 뒤늦게 보고하면 하트비트가 kill 을 지시한다
        var hb = await (await w.PostAsJsonAsync($"/api/workers/{workerId}/heartbeat", new HeartbeatRequest(WorkerStatus.Busy, job.Id, 400, 20000), Json))
            .Content.ReadFromJsonAsync<HeartbeatResponse>(Json);
        hb!.CancelRequested.Should().Be(job.Id);
    }

    [Fact]
    public async Task Admin_can_disable_rotate_and_pretrained_mirror_serves_files_with_hash()
    {
        var admin = await _f.AdminAsync();
        var created = await _f.CreateWorkerAsync(admin, "gpu-admin");
        var w = _f.WorkerClient(created.Token);
        await w.PostAsJsonAsync("/api/workers/register", new RegisterWorkerRequest("gpu-admin", "PC", OkCapabilities(), [TaskType.Detection], "0.1.0"), Json);

        // 관리자가 비활성하면 그 토큰은 인증 자체가 막힌다 — 데이터셋·미러·버전 등록까지 한 번에 끊긴다
        (await admin.PostAsJsonAsync($"/api/workers/{created.WorkerId}/disable", new { reason = "maintenance" }, Json)).StatusCode.Should().Be(HttpStatusCode.OK);
        foreach (var path in new[] { "/api/training-jobs/next?wait=0", "/api/workers/scripts", "/api/pretrained/dfine-small-obj2coco/model.safetensors" })
            (await w.GetAsync(path)).StatusCode.Should().Be(HttpStatusCode.Unauthorized, path);
        (await w.PostAsJsonAsync($"/api/workers/{created.WorkerId}/heartbeat", new HeartbeatRequest(WorkerStatus.Online, null, 400, 0), Json))
            .StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await admin.PostAsync($"/api/workers/{created.WorkerId}/enable", null)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await w.GetAsync("/api/workers/scripts")).StatusCode.Should().Be(HttpStatusCode.OK, "다시 활성화하면 복구된다");

        var rotated = await (await admin.PostAsync($"/api/workers/{created.WorkerId}/rotate-token", null)).Content.ReadFromJsonAsync<CreateWorkerResponse>(Json);
        (await w.GetAsync("/api/workers/scripts")).StatusCode.Should().Be(HttpStatusCode.Unauthorized, "옛 토큰 무효");
        (await _f.WorkerClient(rotated!.Token).GetAsync("/api/workers/scripts")).StatusCode.Should().Be(HttpStatusCode.OK);

        // 사전학습 미러
        (await admin.PostAsJsonAsync("/api/pretrained", new { @ref = "dfine-small-obj2coco", license = "Apache-2.0", source = "hf://ustc-community/dfine-small-obj2coco" }, Json)).StatusCode.Should().Be(HttpStatusCode.Created);
        using var form = new MultipartFormDataContent();
        form.Add(new ByteArrayContent("weights-bytes"u8.ToArray()), "file", "model.safetensors");
        form.Add(new StringContent("{\"a\":1}"), "file2", "config.json");
        var asset = await (await admin.PostAsync("/api/pretrained/dfine-small-obj2coco/files", form)).Content.ReadFromJsonAsync<Contracts.Pretrained.PretrainedAssetDto>(Json);
        asset!.Files.Should().HaveCount(2);
        var file = asset.Files.Single(f => f.FileName == "model.safetensors");
        var dl = await _f.WorkerClient(rotated.Token).GetAsync(file.Url);
        dl.StatusCode.Should().Be(HttpStatusCode.OK);
        dl.Headers.ETag!.Tag.Should().Be($"\"{file.Sha256}\"");
        (await dl.Content.ReadAsStringAsync()).Should().Be("weights-bytes");

        // 작업 배정 응답에 사전학습 파일 목록이 실린다
        var eng = await _f.EngineerAsync();
        var model = await CreateModelAsync(eng, "m-pretrained");
        var ds = await UploadDatasetAsync(eng, "ds-pretrained", zip: MakeYoloDatasetZip(7));
        var job = await (await eng.PostAsJsonAsync("/api/training-jobs", new CreateTrainingJobRequest(ds.Id, model.Id, TrainingScript.TrainDfine, PretrainedRef: "dfine-small-obj2coco", Priority: 30), Json))
            .Content.ReadFromJsonAsync<TrainingJobDto>(Json);
        var w2 = _f.WorkerClient(rotated.Token);
        await w2.PostAsJsonAsync("/api/workers/register", new RegisterWorkerRequest("gpu-admin", "PC", OkCapabilities(), [TaskType.Detection], "0.1.0"), Json);
        var a = await (await w2.GetAsync("/api/training-jobs/next?wait=0")).Content.ReadFromJsonAsync<JobAssignment>(Json);
        a!.Job.Id.Should().Be(job!.Id);
        a.PretrainedFiles.Should().HaveCount(2);
        await w2.PostAsJsonAsync($"/api/training-jobs/{job.Id}/finish", new FinishJobRequest(TrainingJobState.Failed, "cleanup"), Json);
    }

    private async Task SuperviseAsync()
    {
        using var scope = _f.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<TrainingJobService>().SuperviseAsync(CancellationToken.None);
    }
}
