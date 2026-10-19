using System.Net.Http.Json;
using System.Text.Json;
using BODA.VMS.MLOps.Client.Services;
using BODA.VMS.MLOps.Contracts;
using BODA.VMS.MLOps.Contracts.Training;
using BODA.VMS.MLOps.Contracts.Workers;
using BODA.VMS.MLOps.Core.Domain;
using BODA.VMS.MLOps.Server.Auth;
using FluentAssertions;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using static BODA.VMS.MLOps.Tests.Server.MlopsApiFactory;

namespace BODA.VMS.MLOps.Tests.Server;

/// <summary>
/// 학습 화면 실시간 갱신 — 화면이 쓰는 것과 같은 연결 설정으로 서버 허브 메시지를 받아 본다.
/// 서버는 enum 을 camelCase 문자열로 보내므로, 받는 쪽이 같은 규약이 아니면 메시지가 조용히 버려져
/// 화면이 다른 페이지에 다녀와야만 바뀐다.
/// </summary>
public class TrainingHubTests : IClassFixture<MlopsApiFactory>
{
    private readonly MlopsApiFactory _f;
    public TrainingHubTests(MlopsApiFactory f) => _f = f;

    private async Task<HubConnection> ConnectAsViewerAsync()
    {
        var res = await _f.CreateClient().PostAsJsonAsync("/api/auth/dev-token", new { user = "hub-viewer", roles = new[] { Roles.Viewer } });
        res.EnsureSuccessStatusCode();
        var token = (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString()!;

        var conn = TrainingHubClient.UseMlopsProtocol(new HubConnectionBuilder()
                .WithUrl(new Uri(_f.Server.BaseAddress, $"hubs/training?access_token={Uri.EscapeDataString(token)}"), o =>
                {
                    o.HttpMessageHandlerFactory = _ => _f.Server.CreateHandler();
                    o.Transports = HttpTransportType.LongPolling;
                }))
            .Build();
        await conn.StartAsync();
        return conn;
    }

    [Fact]
    public async Task Job_list_receives_JobUpdated_with_enum_fields()
    {
        await using var conn = await ConnectAsViewerAsync();
        var received = new TaskCompletionSource<TrainingJobDto>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var _ = conn.On<TrainingJobDto>("JobUpdated", job => received.TrySetResult(job));

        var created = await QueueJobAsync(await _f.EngineerAsync(), "hub-list");

        var job = await received.Task.WaitAsync(TimeSpan.FromSeconds(10));
        job.Id.Should().Be(created.Id);
        job.State.Should().Be(TrainingJobState.Queued);
        job.TaskType.Should().Be(TaskType.Detection);
        job.Script.Should().Be(TrainingScript.TrainDfine);
    }

    [Fact]
    public async Task Job_detail_receives_Progress_and_Log_from_worker_reports()
    {
        var eng = await _f.EngineerAsync();
        var job = await QueueJobAsync(eng, "hub-detail", priority: 9);
        var admin = await _f.AdminAsync();
        var worker = await _f.CreateWorkerAsync(admin, "hub-gpu");
        var w = _f.WorkerClient(worker.Token);
        (await w.PostAsJsonAsync("/api/workers/register",
            new RegisterWorkerRequest("hub-gpu", "GPU-PC", OkCapabilities(), [TaskType.Detection], "0.1.0"), Json)).EnsureSuccessStatusCode();
        var assigned = (await (await w.GetAsync("/api/training-jobs/next?wait=0")).Content.ReadFromJsonAsync<JobAssignment>(Json))!;
        assigned.Job.Id.Should().Be(job.Id);
        (await w.PostAsync($"/api/training-jobs/{job.Id}/ack", null)).EnsureSuccessStatusCode();

        // 상세 화면처럼 작업 그룹에 들어간 뒤 워커 보고를 기다린다
        await using var conn = await ConnectAsViewerAsync();
        var progress = new TaskCompletionSource<TrainingJobDto>(TaskCreationOptions.RunContinuationsAsynchronously);
        var log = new TaskCompletionSource<IReadOnlyList<JobLogLineDto>>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var p = conn.On<TrainingJobDto>("Progress", j => { if (j.State == TrainingJobState.Running) progress.TrySetResult(j); });
        using var l = conn.On<Guid, IReadOnlyList<JobLogLineDto>>("Log", (_, lines) => log.TrySetResult(lines));
        await conn.InvokeAsync("JoinJob", job.Id);

        (await w.PatchAsJsonAsync($"/api/training-jobs/{job.Id}/progress",
            new ProgressReport(TrainingJobState.Running, 45, 1, 2, 117.5, 0.0008,
                [new LogChunkDto(JobLogLevel.Stdout, "[EPOCH] 1/2", DateTime.UtcNow)]), Json)).EnsureSuccessStatusCode();

        var running = await progress.Task.WaitAsync(TimeSpan.FromSeconds(10));
        running.Progress.Should().Be(45);
        running.CurrentEpoch.Should().Be(1);
        var lines = await log.Task.WaitAsync(TimeSpan.FromSeconds(10));
        lines.Should().ContainSingle(x => x.Text == "[EPOCH] 1/2" && x.Level == JobLogLevel.Stdout);
    }

    private async Task<TrainingJobDto> QueueJobAsync(HttpClient eng, string name, int priority = 0)
    {
        var model = await CreateModelAsync(eng, name);
        var ds = await UploadDatasetAsync(eng, "ds-" + name, zip: MakeYoloDatasetZip(name.Length % 20));
        await EnsurePretrainedAsync(await _f.AdminAsync());
        var res = await eng.PostAsJsonAsync("/api/training-jobs",
            new CreateTrainingJobRequest(ds.Id, model.Id, TrainingScript.TrainDfine, PretrainedRef: SharedPretrainedRef,
                Hyperparams: JsonDocument.Parse("""{"epochs": 2}""").RootElement.Clone(), Priority: priority), Json);
        res.EnsureSuccessStatusCode();
        return (await res.Content.ReadFromJsonAsync<TrainingJobDto>(Json))!;
    }
}
