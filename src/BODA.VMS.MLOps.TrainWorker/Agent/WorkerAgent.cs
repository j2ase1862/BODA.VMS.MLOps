using BODA.VMS.MLOps.Contracts.Training;
using BODA.VMS.MLOps.Contracts.Workers;
using BODA.VMS.MLOps.Core.Domain;
using BODA.VMS.MLOps.TrainWorker.Environment;
using BODA.VMS.MLOps.TrainWorker.Jobs;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BODA.VMS.MLOps.TrainWorker.Agent;

/// <summary>
/// 워커 메인 루프 (Phase 3 §2, §4): 환경 준비 → 자기진단 → 등록 → [하트비트 30s] + [Idle 이면 long-poll → JobRunner].
/// 워커당 동시 1작업. Disabled 면 주기적으로 재등록만 한다.
/// </summary>
public sealed class WorkerAgent(
    ServerClient server, PythonEnvironment python, JobRunner runner, IOptions<WorkerOptions> options,
    IHostApplicationLifetime lifetime, ILogger<WorkerAgent> logger) : BackgroundService
{
    private readonly WorkerOptions _o = options.Value;
    private Guid _workerId;
    private volatile bool _disabled;
    private string? _disabledReason;

    // 메인 루프가 쓰고 하트비트 태스크가 읽는다. 잠그지 않으면 하트비트가 취소를 지시하려는 순간
    // 메인 루프가 CTS 를 dispose 해 ObjectDisposedException 이 나고, 그 예외가 삼켜져 취소가 유실된다.
    private readonly object _jobLock = new();
    private Guid? _currentJobId;
    private CancellationTokenSource? _cancelJob;

    private (Guid? JobId, CancellationTokenSource? Cancel) CurrentJob()
    {
        lock (_jobLock) return (_currentJobId, _cancelJob);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _o.EnsureDirectories();
        if (string.IsNullOrWhiteSpace(_o.ResolveToken()))
        {
            logger.LogCritical("워커 토큰이 없습니다. `BODA.VMS.MLOps.TrainWorker configure --server <url> --token <wk_…>` 로 설정하세요.");
            lifetime.StopApplication();
            return;
        }

        string pythonExe;
        WorkerCapabilities caps;
        while (true)
        {
            try
            {
                pythonExe = await python.EnsureAsync(stoppingToken);
                caps = await python.DiagnoseAsync(pythonExe, PythonEnvironment.ScriptsHash(_o.ScriptsDir), stoppingToken);
                break;
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                logger.LogError(ex, "파이썬 환경 준비 실패 — 60초 후 재시도");
                try { await Task.Delay(TimeSpan.FromSeconds(60), stoppingToken); } catch (OperationCanceledException) { return; }
            }
        }
        if (!caps.DiagnosticsOk)
            logger.LogWarning("자기진단 실패 항목: {Failures}", string.Join("; ", caps.DiagnosticFailures!));

        var reg = await RegisterWithRetryAsync(caps, stoppingToken);
        if (reg is null) return;

        var heartbeat = HeartbeatLoopAsync(reg.HeartbeatSec, stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (_disabled)
                {
                    logger.LogWarning("워커 비활성: {Reason} — 60초 후 재등록", _disabledReason);
                    await Task.Delay(TimeSpan.FromSeconds(60), stoppingToken);
                    caps = await python.DiagnoseAsync(pythonExe, PythonEnvironment.ScriptsHash(_o.ScriptsDir), stoppingToken);
                    reg = await RegisterWithRetryAsync(caps, stoppingToken);
                    continue;
                }

                var assignment = await server.NextJobAsync(reg!.PollIntervalSec, stoppingToken);
                if (assignment is null) continue;

                await RunJobAsync(assignment, caps, pythonExe, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (ServerApiException ex) when (ex.Status == System.Net.HttpStatusCode.Forbidden)
            {
                _disabled = true;
                _disabledReason = ex.Error?.Message ?? ex.Message;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "서버 통신 실패 — {Delay}초 후 재시도", _o.RetryDelaySec);
                try { await Task.Delay(TimeSpan.FromSeconds(_o.RetryDelaySec), stoppingToken); } catch (OperationCanceledException) { break; }
            }
        }
        try { await heartbeat; } catch { }
    }

    private async Task RunJobAsync(JobAssignment assignment, WorkerCapabilities caps, string pythonExe, CancellationToken stoppingToken)
    {
        var cancel = new CancellationTokenSource();
        lock (_jobLock)
        {
            _cancelJob = cancel;
            _currentJobId = assignment.Job.Id;
        }
        logger.LogInformation("작업 수신 {Job} ({Script}, attempt {Attempt})", assignment.Job.Id, assignment.ScriptName, assignment.Job.Attempt);
        try
        {
            await runner.RunAsync(assignment, caps, pythonExe, cancel, stoppingToken);
        }
        finally
        {
            lock (_jobLock)
            {
                _currentJobId = null;
                _cancelJob = null;
            }
            cancel.Dispose();
        }
    }

    private async Task<RegisterWorkerResponse?> RegisterWithRetryAsync(WorkerCapabilities caps, CancellationToken ct)
    {
        var req = new RegisterWorkerRequest(_o.Name, System.Environment.MachineName, caps, _o.TaskTypes ?? Enum.GetValues<TaskType>(), ServerClient.WorkerVersion);
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var res = await server.RegisterAsync(req, ct);
                _workerId = res.WorkerId;
                _disabled = res.Status == WorkerStatus.Disabled;
                _disabledReason = res.DisabledReason;
                logger.LogInformation("등록 완료 workerId={Id} status={Status} scripts={Count} {Reason}", res.WorkerId, res.Status, res.ScriptsManifest.Count, res.DisabledReason);
                return res;
            }
            catch (OperationCanceledException) { return null; }
            catch (ServerApiException ex) when (ex.Status is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden)
            {
                logger.LogCritical(ex, "워커 토큰이 거부되었습니다. Web 관리 화면에서 토큰을 재발급하고 configure 로 갱신하세요.");
                try { await Task.Delay(TimeSpan.FromSeconds(60), ct); } catch (OperationCanceledException) { return null; }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "등록 실패 — {Delay}초 후 재시도", _o.RetryDelaySec);
                try { await Task.Delay(TimeSpan.FromSeconds(_o.RetryDelaySec), ct); } catch (OperationCanceledException) { return null; }
            }
        }
        return null;
    }

    private async Task HeartbeatLoopAsync(int intervalSec, CancellationToken ct)
    {
        var interval = TimeSpan.FromSeconds(Math.Max(5, intervalSec));
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(interval, ct);
                if (_workerId == Guid.Empty) continue;
                var (jobId, _) = CurrentJob();
                var status = _disabled ? WorkerStatus.Disabled : jobId is null ? WorkerStatus.Online : WorkerStatus.Busy;
                var res = await server.HeartbeatAsync(_workerId, new HeartbeatRequest(status, jobId, _o.DiskFreeGB(), 0), ct);
                if (res.Disable && !_disabled) { _disabled = true; _disabledReason = res.DisabledReason; }

                if (res.CancelRequested is { } cancelId)
                {
                    // 지시를 받은 시점과 취소를 거는 시점 사이에 작업이 끝났을 수 있으므로 잠근 채로 확인하고 건다
                    lock (_jobLock)
                    {
                        if (cancelId == _currentJobId && _cancelJob is { IsCancellationRequested: false } cts)
                        {
                            logger.LogWarning("하트비트 취소 지시 — 작업 {Job}", cancelId);
                            cts.Cancel();
                        }
                    }
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "하트비트 실패");
            }
        }
    }
}
