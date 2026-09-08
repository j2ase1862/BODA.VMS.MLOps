using System.Diagnostics;
using System.Text;
using BODA.VMS.MLOps.Core.Domain;
using BODA.VMS.MLOps.Core.Training;
using Microsoft.Extensions.Logging;
using VMS.Core.Services;

namespace BODA.VMS.MLOps.TrainWorker.Jobs;

public sealed record ProcessRunRequest(
    string PythonExe,
    IReadOnlyList<string> Arguments,
    string WorkingDir,
    IReadOnlyDictionary<string, string> Environment,
    TimeSpan MaxSilence,
    TimeSpan MaxDuration);

public sealed record ProcessRunResult(
    int ExitCode, bool Cancelled, bool Stalled, bool TimedOut, bool OomDetected, bool SawDone,
    string? OnnxPath, string? Error, int Epoch, int TotalEpochs, double Progress, double Loss, double Metric)
{
    public bool Succeeded => ExitCode == 0 && !Cancelled && !Stalled && !TimedOut;
}

/// <summary>
/// VMS.Core TrainingService 의 워커 이식본 (Phase 3 §5.2): ArgumentList 로 프로세스 실행, stdout 프로토콜 파싱,
/// 워치독([EPOCH]/[PROGRESS] 무응답 → Stalled, 전체 상한 → TimedOut), 취소 시 프로세스 트리 kill, CUDA OOM 감지.
/// 스크립트 실행 자체와 무관하므로 가짜 스크립트(train_fake.py)로 단위 테스트한다.
/// </summary>
public sealed class TrainingProcessHost(ILogger<TrainingProcessHost> logger)
{
    public async Task<ProcessRunResult> RunAsync(
        ProcessRunRequest req,
        Func<TrainingLineEvent, Task>? onProtocol,
        Func<JobLogLevel, string, Task>? onLog,
        CancellationToken cancel)
    {
        var psi = new ProcessStartInfo
        {
            FileName = req.PythonExe,
            WorkingDirectory = req.WorkingDir,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in req.Arguments) psi.ArgumentList.Add(a);
        foreach (var (k, v) in req.Environment) psi.Environment[k] = v;
        psi.Environment["PYTHONIOENCODING"] = "utf-8";
        psi.Environment["PYTHONUNBUFFERED"] = "1";

        var tracker = new TrainingProtocolTracker();
        bool oom = false, stalled = false, timedOut = false, cancelled = false;
        var lastActivity = Stopwatch.StartNew();
        var total = Stopwatch.StartNew();

        logger.LogInformation("학습 프로세스 시작: {Exe} {Args}", req.PythonExe, string.Join(' ', req.Arguments));
        using var process = Process.Start(psi) ?? throw new InvalidOperationException("학습 프로세스를 시작하지 못했습니다.");

        async Task ReadAsync(StreamReader reader, bool isErr)
        {
            try
            {
                while (true)
                {
                    var line = await reader.ReadLineAsync(CancellationToken.None);
                    if (line is null) break;
                    if (TrainingProtocolTracker.IsCudaOom(line)) oom = true;

                    if (isErr)
                    {
                        if (onLog is not null) await onLog(JobLogLevel.Stderr, line);
                        continue;
                    }

                    var ev = tracker.Apply(line);
                    if (ev.Kind is TrainingOutputKind.Epoch or TrainingOutputKind.Progress) lastActivity.Restart();
                    if (onLog is not null)
                        await onLog(ev.Kind == TrainingOutputKind.Error ? JobLogLevel.Error
                            : line.StartsWith("[WARN]", StringComparison.Ordinal) ? JobLogLevel.Warn : JobLogLevel.Stdout, line);
                    if (ev.Kind != TrainingOutputKind.None && onProtocol is not null) await onProtocol(ev);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "출력 읽기 오류 ({Stream})", isErr ? "stderr" : "stdout");
            }
        }

        var stdoutTask = ReadAsync(process.StandardOutput, false);
        var stderrTask = ReadAsync(process.StandardError, true);

        // 워치독 루프
        while (!process.HasExited)
        {
            if (cancel.IsCancellationRequested) { cancelled = true; Kill(process, "취소 요청"); break; }
            if (lastActivity.Elapsed > req.MaxSilence) { stalled = true; Kill(process, $"무응답 {req.MaxSilence.TotalMinutes:0}분"); break; }
            if (total.Elapsed > req.MaxDuration) { timedOut = true; Kill(process, $"전체 상한 {req.MaxDuration.TotalHours:0}h"); break; }
            try { await Task.Delay(200, CancellationToken.None); } catch { }
        }
        await process.WaitForExitAsync(CancellationToken.None);
        await Task.WhenAll(stdoutTask, stderrTask);

        int exit = process.ExitCode;
        string? error = tracker.LastError;
        if (cancelled) error ??= "취소됨";
        else if (stalled) error = $"학습 스크립트 무응답 ({req.MaxSilence.TotalMinutes:0}분간 [EPOCH]/[PROGRESS] 없음)";
        else if (timedOut) error = $"전체 학습 시간 상한 초과 ({req.MaxDuration.TotalHours:0}h)";
        else if (exit != 0) error ??= $"스크립트 종료 코드 {exit}";
        else if (!tracker.SawDone) error ??= "스크립트가 [DONE] 없이 종료";

        logger.LogInformation("학습 프로세스 종료 exit={Exit} cancelled={C} stalled={S} timeout={T} oom={O} onnx={Onnx}",
            exit, cancelled, stalled, timedOut, oom, tracker.OnnxPath);
        return new ProcessRunResult(exit, cancelled, stalled, timedOut, oom, tracker.SawDone, tracker.OnnxPath, error,
            tracker.CurrentEpoch, tracker.TotalEpochs, tracker.Progress, tracker.Loss, tracker.Metric);
    }

    private void Kill(Process p, string reason)
    {
        try
        {
            logger.LogWarning("학습 프로세스 트리 종료: {Reason}", reason);
            if (!p.HasExited) p.Kill(entireProcessTree: true);
        }
        catch (Exception ex) { logger.LogWarning(ex, "프로세스 종료 실패"); }
    }
}
