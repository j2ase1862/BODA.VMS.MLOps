using BODA.VMS.MLOps.Core.Domain;
using BODA.VMS.MLOps.Core.Training;
using BODA.VMS.MLOps.TrainWorker.Jobs;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using VMS.Core.Services;

namespace BODA.VMS.MLOps.Tests.Worker;

/// <summary>Phase 3 §11 프로토콜 단위: 가짜 train_fake.py 로 정상·오류·무응답(워치독)·취소·OOM 경로</summary>
public class TrainingProcessHostTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "mlops-host-" + Guid.NewGuid().ToString("N"));
    private readonly string _dataset;
    private readonly string _output;

    public TrainingProcessHostTests()
    {
        _dataset = Path.Combine(_root, "ds");
        _output = Path.Combine(_root, "out");
        Directory.CreateDirectory(_dataset);
        File.WriteAllText(Path.Combine(_dataset, "data.yaml"), "names: [good, defect]");
    }

    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

    private ProcessRunRequest Request(string mode, int epochs = 3, TimeSpan? silence = null, TimeSpan? max = null, int batch = 8)
    {
        var args = TrainingArgumentList.Build(new TrainingArgumentList.Request(
            TrainingScript.TrainDfine, PythonLocator.FakeScript, _dataset, _output, null, 7,
            new Dictionary<string, string> { ["epochs"] = epochs.ToString(), ["batch_size"] = batch.ToString() }));
        var env = new Dictionary<string, string> { ["FAKE_MODE"] = mode, ["FAKE_EPOCH_DELAY"] = "0.02", ["FAKE_STALL_SEC"] = "30" };
        return new ProcessRunRequest(PythonLocator.Path!, args, _root, env, silence ?? TimeSpan.FromMinutes(5), max ?? TimeSpan.FromMinutes(5));
    }

    private static bool NoPython => PythonLocator.Path is null;

    [Fact]
    public async Task Ok_mode_completes_with_onnx_and_events()
    {
        if (NoPython) return;
        var host = new TrainingProcessHost(NullLogger<TrainingProcessHost>.Instance);
        var kinds = new List<TrainingOutputKind>();
        var logs = new List<(JobLogLevel, string)>();

        var r = await host.RunAsync(Request("ok"), ev => { kinds.Add(ev.Kind); return Task.CompletedTask; },
            (l, s) => { logs.Add((l, s)); return Task.CompletedTask; }, CancellationToken.None);

        r.Succeeded.Should().BeTrue(r.Error);
        r.SawDone.Should().BeTrue();
        r.Epoch.Should().Be(3);
        r.TotalEpochs.Should().Be(3);
        r.Progress.Should().Be(100);
        r.OnnxPath.Should().NotBeNull();
        File.Exists(r.OnnxPath!).Should().BeTrue();
        kinds.Should().Contain(TrainingOutputKind.Epoch).And.Contain(TrainingOutputKind.Onnx).And.Contain(TrainingOutputKind.Done);
        logs.Should().Contain(l => l.Item1 == JobLogLevel.Stdout && l.Item2.StartsWith("[INFO] fake trainer"));
        File.Exists(Path.Combine(_output, "best", "vms_train_info.json")).Should().BeTrue();
    }

    [Fact]
    public async Task Error_mode_reports_script_error()
    {
        if (NoPython) return;
        var host = new TrainingProcessHost(NullLogger<TrainingProcessHost>.Instance);
        var r = await host.RunAsync(Request("error"), null, null, CancellationToken.None);
        r.Succeeded.Should().BeFalse();
        r.ExitCode.Should().Be(1);
        r.Error.Should().Contain("simulated failure");
        r.SawDone.Should().BeFalse();
    }

    [Fact]
    public async Task Stall_mode_is_killed_by_watchdog()
    {
        if (NoPython) return;
        var host = new TrainingProcessHost(NullLogger<TrainingProcessHost>.Instance);
        var r = await host.RunAsync(Request("stall", silence: TimeSpan.FromSeconds(2)), null, null, CancellationToken.None);
        r.Stalled.Should().BeTrue();
        r.Succeeded.Should().BeFalse();
        r.Error.Should().Contain("무응답");
    }

    [Fact]
    public async Task Cancellation_kills_process_tree()
    {
        if (NoPython) return;
        var host = new TrainingProcessHost(NullLogger<TrainingProcessHost>.Instance);
        using var cts = new CancellationTokenSource();
        var task = host.RunAsync(Request("stall"), null, null, cts.Token);
        await Task.Delay(1500);
        cts.Cancel();
        var r = await task;
        r.Cancelled.Should().BeTrue();
        r.Succeeded.Should().BeFalse();
    }

    [Fact]
    public async Task Oom_is_detected_from_stderr()
    {
        if (NoPython) return;
        var host = new TrainingProcessHost(NullLogger<TrainingProcessHost>.Instance);
        var r = await host.RunAsync(Request("oom"), null, null, CancellationToken.None);
        r.OomDetected.Should().BeTrue();
        r.Succeeded.Should().BeFalse();
    }

    [Fact]
    public async Task Nodone_exit_zero_is_not_success()
    {
        if (NoPython) return;
        var host = new TrainingProcessHost(NullLogger<TrainingProcessHost>.Instance);
        var r = await host.RunAsync(Request("nodone"), null, null, CancellationToken.None);
        r.ExitCode.Should().Be(0);
        r.SawDone.Should().BeFalse();
        r.Error.Should().Contain("[DONE]");
    }
}
