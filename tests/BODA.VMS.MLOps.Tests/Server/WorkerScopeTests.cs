using System.Net;
using System.Net.Http.Json;
using BODA.VMS.MLOps.Contracts;
using BODA.VMS.MLOps.Contracts.Models;
using BODA.VMS.MLOps.Contracts.Training;
using BODA.VMS.MLOps.Contracts.Workers;
using BODA.VMS.MLOps.Core.Domain;
using BODA.VMS.MLOps.Server.Data;
using BODA.VMS.MLOps.Server.Services;
using BODA.VMS.MLOps.Tests.TestAssets;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static BODA.VMS.MLOps.Tests.Server.MlopsApiFactory;

namespace BODA.VMS.MLOps.Tests.Server;

/// <summary>
/// 워커 서비스 계정의 범위 (Phase 3 §8): 워커 API·데이터셋 export·사전학습 미러·아티팩트 업로드만.
/// 레시피와 생산 이력은 볼 수 없어야 한다 — GPU PC 토큰 하나가 새도 배포 지형 전체가 드러나지 않게.
/// </summary>
public class WorkerScopeTests : IClassFixture<MlopsApiFactory>
{
    private readonly MlopsApiFactory _f;
    public WorkerScopeTests(MlopsApiFactory f) => _f = f;

    [Fact]
    public async Task Worker_token_cannot_read_recipes_models_or_other_jobs()
    {
        var admin = await _f.AdminAsync();
        var eng = await _f.EngineerAsync();
        var model = await CreateModelAsync(eng, "scope-model");
        var version = (await (await UploadVersionAsync(eng, model.Id, OnnxStubs.Bytes(OnnxStubs.DeployWithMeta)))
            .Content.ReadFromJsonAsync<ModelVersionDto>(Json))!;
        await eng.PutAsJsonAsync($"/api/recipes/SCOPE-1/tools/T1/model", new SetBindingRequest(BindingMode.Pinned, version.Id), Json);

        var created = await _f.CreateWorkerAsync(admin, "scope-worker");
        var w = _f.WorkerClient(created.Token);

        string[] denied =
        [
            "/api/models",
            $"/api/models/{model.Id}",
            $"/api/models/{model.Id}/resolve?stage=candidate",
            $"/api/model-versions/{version.Id}",
            $"/api/model-versions/{version.Id}/artifact",
            "/api/recipes/SCOPE-1/model-bindings",
            "/api/recipes/SCOPE-1/model-bindings/payload",
            "/api/workers",
            "/api/training-jobs",
        ];
        foreach (var path in denied)
            (await w.GetAsync(path)).StatusCode.Should().Be(HttpStatusCode.Forbidden, $"워커는 {path} 를 볼 수 없어야 한다");
    }

    [Fact]
    public async Task Worker_token_can_reach_only_what_it_needs_to_run()
    {
        var admin = await _f.AdminAsync();
        var eng = await _f.EngineerAsync();
        var ds = await UploadDatasetAsync(eng, "scope-ds", zip: MakeYoloDatasetZip(9));
        await admin.PostAsJsonAsync("/api/pretrained", new { @ref = "scope-weights" }, Json);
        using (var form = new MultipartFormDataContent())
        {
            form.Add(new ByteArrayContent("w"u8.ToArray()), "file", "model.safetensors");
            (await admin.PostAsync("/api/pretrained/scope-weights/files", form)).EnsureSuccessStatusCode();
        }

        var created = await _f.CreateWorkerAsync(admin, "scope-worker-allow");
        var w = _f.WorkerClient(created.Token);

        string[] allowed =
        [
            "/api/workers/scripts",
            "/api/workers/scripts/train_dfine.py",
            $"/api/dataset-versions/{ds.Id}/export?format=yolo",
            "/api/pretrained/scope-weights/model.safetensors",
        ];
        foreach (var path in allowed)
            (await w.GetAsync(path)).StatusCode.Should().Be(HttpStatusCode.OK, $"워커는 {path} 가 필요하다");
    }
}

/// <summary>
/// 감독자 재큐와 워커 보고가 같은 행을 동시에 고칠 때, 뒤늦은 쪽이 조용히 덮어쓰지 않아야 한다.
/// 동시성 토큰이 없으면 워커는 학습 중인데 서버는 미배정으로 보고 같은 작업을 두 번째 워커에 준다.
/// </summary>
public class JobConcurrencyTests : IClassFixture<MlopsApiFactory>
{
    private readonly MlopsApiFactory _f;
    public JobConcurrencyTests(MlopsApiFactory f) => _f = f;

    [Fact]
    public async Task Stale_worker_report_loses_to_requeue_with_409()
    {
        var eng = await _f.EngineerAsync();
        var admin = await _f.AdminAsync();
        var model = await CreateModelAsync(eng, "conc-model");
        var ds = await UploadDatasetAsync(eng, "conc-ds", zip: MakeYoloDatasetZip(11));
        var job = (await (await eng.PostAsJsonAsync("/api/training-jobs",
            new CreateTrainingJobRequest(ds.Id, model.Id, TrainingScript.TrainDfine, Priority: 50), Json))
            .Content.ReadFromJsonAsync<TrainingJobDto>(Json))!;

        var created = await _f.CreateWorkerAsync(admin, "conc-worker", [TaskType.Detection]);
        var w = _f.WorkerClient(created.Token);
        await w.PostAsJsonAsync("/api/workers/register",
            new RegisterWorkerRequest("conc-worker", "PC", OkCapabilities(), [TaskType.Detection], "0.1.0"), Json);
        var a = await (await w.GetAsync("/api/training-jobs/next?wait=0")).Content.ReadFromJsonAsync<JobAssignment>(Json);
        a!.Job.Id.Should().Be(job.Id);
        await w.PostAsync($"/api/training-jobs/{job.Id}/ack", null);

        // 서버가 다른 경로로 이 작업을 재큐했다고 하자 (감독자의 하트비트 소실 처리와 같은 상황)
        using (var scope = _f.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MlopsDbContext>();
            var row = await db.TrainingJobs.FirstAsync(j => j.Id == job.Id);
            row.State = TrainingJobState.Queued;
            row.WorkerId = null;
            await db.SaveChangesAsync();
        }

        // 원래 워커의 뒤늦은 보고는 거부되어야 한다 (자기 것이 아니게 되었으므로 403)
        var late = await w.PatchAsJsonAsync($"/api/training-jobs/{job.Id}/progress",
            new ProgressReport(TrainingJobState.Running, 10, 1, 3), Json);
        late.StatusCode.Should().Be(HttpStatusCode.Forbidden);

        var after = await eng.GetFromJsonAsync<TrainingJobDto>($"/api/training-jobs/{job.Id}", Json);
        after!.State.Should().Be(TrainingJobState.Queued, "워커 보고가 재큐를 되돌리면 안 된다");
    }

    [Fact]
    public async Task Artifacts_and_finish_cannot_skip_the_state_machine()
    {
        var eng = await _f.EngineerAsync();
        var admin = await _f.AdminAsync();
        var model = await CreateModelAsync(eng, "skip-model");
        var ds = await UploadDatasetAsync(eng, "skip-ds", zip: MakeYoloDatasetZip(12));
        var job = (await (await eng.PostAsJsonAsync("/api/training-jobs",
            new CreateTrainingJobRequest(ds.Id, model.Id, TrainingScript.TrainDfine, Priority: 60), Json))
            .Content.ReadFromJsonAsync<TrainingJobDto>(Json))!;

        var created = await _f.CreateWorkerAsync(admin, "skip-worker", [TaskType.Detection]);
        var w = _f.WorkerClient(created.Token);
        await w.PostAsJsonAsync("/api/workers/register",
            new RegisterWorkerRequest("skip-worker", "PC", OkCapabilities(), [TaskType.Detection], "0.1.0"), Json);
        (await (await w.GetAsync("/api/training-jobs/next?wait=0")).Content.ReadFromJsonAsync<JobAssignment>(Json))!.Job.Id.Should().Be(job.Id);

        // Assigned 상태에서 아티팩트만 올려 Succeeded 로 건너뛰려는 시도
        using var form = new MultipartFormDataContent();
        form.Add(new ByteArrayContent(OnnxStubs.Bytes(OnnxStubs.DeployWithMeta)), "onnx", "best.onnx");
        var early = await w.PostAsync($"/api/training-jobs/{job.Id}/artifacts", form);
        early.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await ErrorAsync(early))!.Code.Should().Be(ErrorCodes.InvalidJobTransition);

        var finish = await w.PostAsJsonAsync($"/api/training-jobs/{job.Id}/finish", new FinishJobRequest(TrainingJobState.Succeeded), Json);
        finish.StatusCode.Should().Be(HttpStatusCode.BadRequest, "결과 버전이 없으면 성공으로 끝낼 수 없다");

        // 정리: 실패로 종료
        (await w.PostAsJsonAsync($"/api/training-jobs/{job.Id}/finish", new FinishJobRequest(TrainingJobState.Failed, "cleanup"), Json))
            .StatusCode.Should().Be(HttpStatusCode.OK);
    }
}

public class ZipAndAllowlistTests
{
    [Fact]
    public void Allowlist_rejects_options_that_pull_in_other_files()
    {
        TrainWorker.Environment.PackageAllowlist.FindDisallowed(["-r evil.txt"]).Should().ContainSingle();
        TrainWorker.Environment.PackageAllowlist.FindDisallowed(["-e ."]).Should().ContainSingle();
        TrainWorker.Environment.PackageAllowlist.FindDisallowed(["-c constraints.txt"]).Should().ContainSingle();
        TrainWorker.Environment.PackageAllowlist.FindDisallowed(["--extra-index-url https://download.pytorch.org/whl/cu124"]).Should().BeEmpty();
        TrainWorker.Environment.PackageAllowlist.FindDisallowed(["# comment", "", "torch>=2.5"]).Should().BeEmpty();
        TrainWorker.Environment.PackageAllowlist.FindDisallowed(["evilpkg==1.0"]).Should().ContainSingle();
    }

    [Fact]
    public void Shipped_requirements_file_passes_its_own_allowlist()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "scripts", "requirements-allowlist.txt");
        if (!File.Exists(path)) return;
        TrainWorker.Environment.PackageAllowlist.FindDisallowed(File.ReadAllLines(path)).Should().BeEmpty();
    }
}
