using System.Net.Http.Json;
using System.Text.Json;
using BODA.VMS.MLOps.Contracts.Models;
using BODA.VMS.MLOps.Contracts.Training;
using BODA.VMS.MLOps.Contracts.Workers;
using BODA.VMS.MLOps.Core.Domain;
using BODA.VMS.MLOps.Tests.Server;
using BODA.VMS.MLOps.TrainWorker;
using BODA.VMS.MLOps.TrainWorker.Agent;
using BODA.VMS.MLOps.TrainWorker.Jobs;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using static BODA.VMS.MLOps.Tests.Server.MlopsApiFactory;

namespace BODA.VMS.MLOps.Tests.Worker;

/// <summary>
/// 워커 ↔ 서버 E2E (GPU 불필요): 인메모리 서버 + 실제 JobRunner + train_fake.py(train_dfine.py 로 배포)
///   작업 제출 → 워커 long-poll → 데이터셋 zip 다운로드·안전 해제 → 스크립트 해시 검증 → 학습 → 아티팩트 업로드 → ModelVersion(Candidate) → Succeeded
/// </summary>
public class WorkerEndToEndTests : IClassFixture<MlopsApiFactory>, IDisposable
{
    private readonly MlopsApiFactory _f;
    private readonly string _cache = Path.Combine(Path.GetTempPath(), "mlops-worker-" + Guid.NewGuid().ToString("N"));

    public WorkerEndToEndTests(MlopsApiFactory f) => _f = f;
    public void Dispose() { try { Directory.Delete(_cache, true); } catch { } }

    private (JobRunner Runner, ServerClient Client, WorkerOptions Options) BuildWorker(string token, string mode = "ok")
    {
        var options = new WorkerOptions
        {
            ServerUrl = "http://localhost", Token = token, Name = "e2e-worker", CacheRoot = _cache,
            PythonExe = PythonLocator.Path, SkipDiagnostics = true, AllowCpuOnly = true, MaxSilenceMinutes = 1, MaxDurationHours = 1,
        };
        options.EnsureDirectories();
        System.Environment.SetEnvironmentVariable("FAKE_MODE", mode);
        System.Environment.SetEnvironmentVariable("FAKE_EPOCH_DELAY", "0.02");
        var opt = Options.Create(options);
        var http = _f.CreateClient(); // 인메모리 TestServer 로 라우팅
        var client = new ServerClient(http, opt, NullLogger<ServerClient>.Instance);
        var runner = new JobRunner(client,
            new DatasetCache(client, opt, NullLogger<DatasetCache>.Instance),
            new PretrainedCache(client, opt, NullLogger<PretrainedCache>.Instance),
            new ScriptStore(client, opt, NullLogger<ScriptStore>.Instance),
            new TrainingProcessHost(NullLogger<TrainingProcessHost>.Instance),
            opt, NullLogger<JobRunner>.Instance);
        return (runner, client, options);
    }

    [Fact]
    public async Task Submit_train_upload_register_candidate()
    {
        if (PythonLocator.Path is null) return;

        var eng = await _f.EngineerAsync();
        var admin = await _f.AdminAsync();
        var model = await CreateModelAsync(eng, "e2e-model");
        var ds = await UploadDatasetAsync(eng, "e2e-ds", zip: MakeYoloDatasetZip(3));
        var job = (await (await eng.PostAsJsonAsync("/api/training-jobs",
                new CreateTrainingJobRequest(ds.Id, model.Id, TrainingScript.TrainDfine, Hyperparams: JsonDocument.Parse("""{"epochs": 3, "batch_size": 4}""").RootElement, Seed: 11, Priority: 100), Json))
            .Content.ReadFromJsonAsync<TrainingJobDto>(Json))!;

        var created = await _f.CreateWorkerAsync(admin, "e2e-worker", [TaskType.Detection]);
        var (runner, client, _) = BuildWorker(created.Token);

        var reg = await client.RegisterAsync(new RegisterWorkerRequest("e2e-worker", "PC", OkCapabilities(), [TaskType.Detection], ServerClient.WorkerVersion), CancellationToken.None);
        reg.Status.Should().Be(WorkerStatus.Online);

        var assignment = await client.NextJobAsync(0, CancellationToken.None);
        assignment.Should().NotBeNull();
        assignment!.Job.Id.Should().Be(job.Id);

        using var cancel = new CancellationTokenSource();
        await runner.RunAsync(assignment, OkCapabilities(), PythonLocator.Path, cancel, CancellationToken.None);

        var done = await eng.GetFromJsonAsync<TrainingJobDto>($"/api/training-jobs/{job.Id}", Json);
        done!.State.Should().Be(TrainingJobState.Succeeded, done.Error);
        done.Progress.Should().Be(100);
        done.CurrentEpoch.Should().Be(3);
        done.ResultModelVersionId.Should().NotBeNull();
        done.Reproducibility!.DatasetManifestHash.Should().Be(ds.ManifestHash);
        done.Reproducibility.ScriptSha256.Should().Be(assignment.ScriptSha256);
        done.Reproducibility.Seed.Should().Be(11);
        done.Reproducibility.Hyperparams["batch_size"].Should().Be("4");

        var version = await eng.GetFromJsonAsync<ModelVersionDto>($"/api/model-versions/{done.ResultModelVersionId}", Json);
        version!.Format.Should().Be(ModelFormat.DFine);
        version.Stage.Should().Be(ModelStage.Candidate);
        version.Source.Should().Be(VersionSource.TrainingJob);
        version.Classes.Should().Equal("good", "defect");
        version.Metrics.Should().ContainKey("map50");
        version.InputSize.Should().Be(640);

        var artifacts = await eng.GetFromJsonAsync<List<JobArtifactDto>>($"/api/training-jobs/{job.Id}/artifacts", Json);
        artifacts!.Select(a => a.Kind).Should().Contain([JobArtifactKind.Onnx, JobArtifactKind.Metrics, JobArtifactKind.TrainInfo, JobArtifactKind.Log]);

        var logs = await eng.GetFromJsonAsync<JobLogPage>($"/api/training-jobs/{job.Id}/logs?take=1000", Json);
        logs!.Lines.Should().Contain(l => l.Text.Contains("[DONE]"));
        logs.Lines.Should().Contain(l => l.Text.Contains("[EPOCH] 3/3"));

        // 데이터셋 캐시가 남아 두 번째 실행은 다운로드 없이 재사용된다 (.complete 마커)
        Directory.GetFiles(Path.Combine(_cache, "datasets"), ".complete", SearchOption.AllDirectories).Should().HaveCount(1);
        File.Exists(Path.Combine(_cache, "scripts", "train_dfine.py")).Should().BeTrue();

        (await eng.GetFromJsonAsync<WorkerDto>($"/api/workers/{created.WorkerId}", Json))!.Status.Should().Be(WorkerStatus.Online);
    }

    [Fact]
    public async Task Script_error_marks_job_failed_with_script_error()
    {
        if (PythonLocator.Path is null) return;

        var eng = await _f.EngineerAsync();
        var admin = await _f.AdminAsync();
        var model = await CreateModelAsync(eng, "e2e-fail-model");
        var ds = await UploadDatasetAsync(eng, "e2e-fail-ds", zip: MakeYoloDatasetZip(4));
        var job = (await (await eng.PostAsJsonAsync("/api/training-jobs",
                new CreateTrainingJobRequest(ds.Id, model.Id, TrainingScript.TrainDfine, Hyperparams: JsonDocument.Parse("""{"epochs": 3}""").RootElement, Priority: 90), Json))
            .Content.ReadFromJsonAsync<TrainingJobDto>(Json))!;

        var created = await _f.CreateWorkerAsync(admin, "e2e-fail-worker", [TaskType.Detection]);
        var (runner, client, _) = BuildWorker(created.Token, mode: "error");
        await client.RegisterAsync(new RegisterWorkerRequest("e2e-fail-worker", "PC", OkCapabilities(), [TaskType.Detection], ServerClient.WorkerVersion), CancellationToken.None);
        var assignment = (await client.NextJobAsync(0, CancellationToken.None))!;
        assignment.Job.Id.Should().Be(job.Id);

        using var cancel = new CancellationTokenSource();
        await runner.RunAsync(assignment, OkCapabilities(), PythonLocator.Path!, cancel, CancellationToken.None);

        var done = await eng.GetFromJsonAsync<TrainingJobDto>($"/api/training-jobs/{job.Id}", Json);
        done!.State.Should().Be(TrainingJobState.Failed);
        done.FailureKind.Should().Be(FailureKinds.ScriptError);
        done.Error.Should().Contain("simulated failure");
        done.ResultModelVersionId.Should().BeNull();
    }
}
