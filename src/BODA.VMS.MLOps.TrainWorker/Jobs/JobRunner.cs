using System.Globalization;
using BODA.VMS.MLOps.Contracts.Training;
using BODA.VMS.MLOps.Contracts.Workers;
using BODA.VMS.MLOps.Core.Domain;
using BODA.VMS.MLOps.Core.Training;
using BODA.VMS.MLOps.TrainWorker.Agent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using VMS.Core.Services;

namespace BODA.VMS.MLOps.TrainWorker.Jobs;

/// <summary>
/// JobRunner (Phase 3 §5): Preparing(데이터셋·사전학습·스크립트·출력 폴더·디스크) → Running(TrainingProcessHost, 1/s 코얼레싱 보고)
/// → Exporting/Uploading(best.onnx 검증·아티팩트 업로드 재시도 3회) → finish(재현성 레코드).
/// CUDA OOM 은 batch_size 반감으로 1회 재실행, 취소는 하트비트 응답 또는 progress 응답으로 전달된다.
/// </summary>
public sealed class JobRunner(
    ServerClient server, DatasetCache datasets, PretrainedCache pretrained, ScriptStore scripts, TrainingProcessHost host,
    IOptions<WorkerOptions> options, ILogger<JobRunner> logger)
{
    private readonly WorkerOptions _o = options.Value;

    public async Task RunAsync(JobAssignment a, WorkerCapabilities caps, string pythonExe, CancellationTokenSource cancelJob, CancellationToken shutdown)
    {
        var job = a.Job;
        var jobDir = Path.Combine(_o.JobsDir, job.Id.ToString("N"));
        var outputDir = Path.Combine(jobDir, "output");
        Directory.CreateDirectory(outputDir);
        var localLog = Path.Combine(jobDir, "train.log");
        await using var logWriter = new StreamWriter(localLog, append: true) { AutoFlush = true };

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancelJob.Token, shutdown);
        var ct = linked.Token;
        var reporter = new ProgressReporter(server, job.Id, logger, cancelJob);

        string? datasetDir = null, scriptPath = null, pretrainedDir = null;
        var hyperparams = new Dictionary<string, string>(job.Hyperparams, StringComparer.Ordinal);

        try
        {
            await server.AckAsync(job.Id, ct);
            await logWriter.WriteLineAsync($"[worker] job {job.Id} ack · script {a.ScriptName} · dataset {a.DatasetManifestHash[..12]}");

            // ── Preparing ──
            if (!HyperparamWhitelist.ValidateNormalized(job.Script, hyperparams, out var hpErrors))
                throw new PrepareException("하이퍼파라미터 화이트리스트 위반: " + string.Join("; ", hpErrors));

            var free = _o.DiskFreeGB() * 1e9;
            if (free < a.DatasetSizeBytes * 3L)
                throw new PrepareException($"디스크 여유 부족: {free / 1e9:0.0}GB < 데이터셋 ×3 ({a.DatasetSizeBytes * 3 / 1e9:0.0}GB)");

            await reporter.LogAsync(JobLogLevel.Info, "데이터셋 준비 중", ct);
            datasetDir = await datasets.EnsureAsync(a, ct);
            pretrainedDir = await pretrained.EnsureAsync(a.PretrainedFiles, ct);
            scriptPath = await scripts.EnsureAsync(a.ScriptName, a.ScriptSha256, a.ScriptUrl, ct);
            await reporter.LogAsync(JobLogLevel.Info, $"준비 완료 · dataset={datasetDir} · script={Path.GetFileName(scriptPath)}", ct);
            await reporter.FlushAsync(null, ct);

            // ── Running (OOM 시 batch 반감 1회) ──
            ProcessRunResult result;
            int localAttempt = 0;
            while (true)
            {
                result = await RunOnceAsync(a, hyperparams, pythonExe, scriptPath, datasetDir, pretrainedDir, outputDir, reporter, logWriter, ct);
                if (result.OomDetected && !result.Cancelled && !result.Succeeded && localAttempt == 0
                    && hyperparams.TryGetValue("batch_size", out var bs) && int.TryParse(bs, out var b) && b > 1)
                {
                    localAttempt++;
                    hyperparams["batch_size"] = (b / 2).ToString(CultureInfo.InvariantCulture);
                    await reporter.LogAsync(JobLogLevel.Warn, $"CUDA OOM 감지 → batch_size {b} → {b / 2} 로 1회 재시도", ct);
                    continue;
                }
                break;
            }

            if (result.Cancelled || cancelJob.IsCancellationRequested)
            {
                await FinishAsync(job.Id, TrainingJobState.Cancelled, "취소됨", null, null, shutdown);
                return;
            }
            if (!result.Succeeded || !result.SawDone)
            {
                var kind = result.Stalled ? FailureKinds.Stalled : result.TimedOut ? FailureKinds.Timeout : result.OomDetected ? FailureKinds.Oom : FailureKinds.ScriptError;
                await FinishAsync(job.Id, TrainingJobState.Failed, result.Error ?? "학습 실패", kind, null, shutdown);
                return;
            }

            // ── Exporting / Uploading ──
            var onnx = ResolveOnnx(result.OnnxPath, outputDir)
                       ?? throw new UploadException("best.onnx 를 찾을 수 없습니다 ([ONNX] 경로·output/best.onnx 모두 없음)");
            await reporter.FlushAsync(TrainingJobState.Exporting, ct);

            await logWriter.FlushAsync(ct); // 업로드 전에 지금까지의 로그를 파일에 확정
            var files = CollectArtifacts(onnx, outputDir, localLog);
            var artifacts = await UploadWithRetryAsync(job.Id, files, ct);
            foreach (var w in artifacts.Warnings) logger.LogWarning("아티팩트 경고: {W}", w);

            var repro = new ReproducibilityRecord(a.DatasetManifestHash, a.ScriptSha256, caps.Packages, job.Seed,
                _o.Name, caps.GpuName, caps.PythonVersion, hyperparams);
            await FinishAsync(job.Id, TrainingJobState.Succeeded, null, null, repro, shutdown);
            logger.LogInformation("작업 {Job} 완료 → ModelVersion {V}", job.Id, artifacts.ModelVersionId);
        }
        catch (OperationCanceledException) when (cancelJob.IsCancellationRequested && !shutdown.IsCancellationRequested)
        {
            await FinishAsync(job.Id, TrainingJobState.Cancelled, "취소됨", null, null, shutdown);
        }
        catch (OperationCanceledException) when (shutdown.IsCancellationRequested)
        {
            logger.LogWarning("워커 종료로 작업 {Job} 중단 — 서버가 하트비트 소실로 재큐한다", job.Id);
        }
        catch (PrepareException ex)
        {
            logger.LogError(ex, "준비 실패");
            await FinishAsync(job.Id, TrainingJobState.Failed, ex.Message, FailureKinds.PrepareError, null, shutdown);
        }
        catch (UploadException ex)
        {
            logger.LogError(ex, "업로드 실패");
            await FinishAsync(job.Id, TrainingJobState.Failed, ex.Message, FailureKinds.UploadError, null, shutdown);
        }
        catch (ServerApiException ex) when (ex.IsClientError)
        {
            logger.LogError(ex, "서버 거부");
            await FinishAsync(job.Id, TrainingJobState.Failed, ex.Message, FailureKinds.UploadError, null, shutdown);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "작업 실행 오류");
            await FinishAsync(job.Id, TrainingJobState.Failed, ex.Message, FailureKinds.PrepareError, null, shutdown);
        }
        finally
        {
            CleanupOldJobs();
        }
    }

    private async Task<ProcessRunResult> RunOnceAsync(JobAssignment a, Dictionary<string, string> hyperparams, string pythonExe, string scriptPath,
        string datasetDir, string? pretrainedDir, string outputDir, ProgressReporter reporter, StreamWriter logWriter, CancellationToken ct)
    {
        var job = a.Job;
        var args = TrainingArgumentList.Build(new TrainingArgumentList.Request(
            job.Script, scriptPath, datasetDir, outputDir, pretrainedDir ?? job.Backbone, job.Seed, hyperparams,
            ExportOnnx: true, Device: null));

        var env = new Dictionary<string, string>
        {
            ["HF_HUB_OFFLINE"] = "1",
            ["TRANSFORMERS_OFFLINE"] = "1",
            ["CUDA_VISIBLE_DEVICES"] = _o.GpuIndex.ToString(CultureInfo.InvariantCulture),
            ["OMP_NUM_THREADS"] = "4",
            ["VMS_JOB_ID"] = job.Id.ToString("N"),
        };

        await reporter.FlushAsync(TrainingJobState.Running, ct);
        await logWriter.WriteLineAsync($"[worker] run: {pythonExe} {string.Join(' ', args)}");

        var result = await host.RunAsync(
            new ProcessRunRequest(pythonExe, args, Path.GetDirectoryName(scriptPath)!, env,
                TimeSpan.FromMinutes(_o.MaxSilenceMinutes), TimeSpan.FromHours(_o.MaxDurationHours)),
            onProtocol: ev =>
            {
                reporter.Update(ev);
                return ev.Kind == TrainingOutputKind.Onnx ? reporter.FlushAsync(TrainingJobState.Exporting, ct) : reporter.MaybeFlushAsync(ct);
            },
            onLog: async (level, line) =>
            {
                await logWriter.WriteLineAsync(line);
                await reporter.LogAsync(level, line, ct);
            },
            cancel: ct);

        await reporter.FlushAsync(null, CancellationToken.None);
        return result;
    }

    private async Task<ArtifactsResponse> UploadWithRetryAsync(Guid jobId, IReadOnlyList<(string, string)> files, CancellationToken ct)
    {
        Exception? last = null;
        for (int attempt = 1; attempt <= 3; attempt++)
        {
            try { return await server.UploadArtifactsAsync(jobId, files, ct); }
            catch (ServerApiException ex) when (ex.IsClientError) { throw new UploadException($"서버가 아티팩트를 거부: {ex.Error?.Code} {ex.Error?.Message}", ex); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                last = ex;
                logger.LogWarning(ex, "아티팩트 업로드 실패 (시도 {A}/3)", attempt);
                await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, attempt) * 2), ct);
            }
        }
        throw new UploadException("아티팩트 업로드 3회 실패: " + last?.Message, last);
    }

    private async Task FinishAsync(Guid jobId, TrainingJobState state, string? error, string? kind, ReproducibilityRecord? repro, CancellationToken ct)
    {
        for (int attempt = 1; attempt <= 5; attempt++)
        {
            try
            {
                await server.FinishAsync(jobId, new FinishJobRequest(state, error, kind, repro), ct);
                return;
            }
            catch (ServerApiException ex) when (ex.IsClientError)
            {
                logger.LogWarning(ex, "finish 거부 — 서버가 이미 상태를 바꿨을 수 있음");
                return;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "finish 실패 (시도 {A}/5)", attempt);
                try { await Task.Delay(TimeSpan.FromSeconds(5 * attempt), ct); } catch (OperationCanceledException) { return; }
            }
        }
    }

    private static string? ResolveOnnx(string? reported, string outputDir)
    {
        if (!string.IsNullOrWhiteSpace(reported))
        {
            var p = Path.IsPathRooted(reported) ? reported : Path.Combine(outputDir, reported);
            if (File.Exists(p) && new FileInfo(p).Length > 0) return p;
        }
        var direct = Path.Combine(outputDir, "best.onnx");
        if (File.Exists(direct)) return direct;
        return Directory.Exists(outputDir)
            ? Directory.GetFiles(outputDir, "*.onnx", SearchOption.AllDirectories).OrderByDescending(f => new FileInfo(f).LastWriteTimeUtc).FirstOrDefault()
            : null;
    }

    private static List<(string Field, string Path)> CollectArtifacts(string onnx, string outputDir, string localLog)
    {
        var list = new List<(string, string)> { ("onnx", onnx) };
        string? Find(params string[] names) => names.Select(n => Directory.GetFiles(outputDir, n, SearchOption.AllDirectories).FirstOrDefault()).FirstOrDefault(f => f is not null);
        if (Find("metrics.json") is { } m) list.Add(("metrics", m));
        if (Find("curves.png", "results.png", "training_curve.png") is { } c) list.Add(("curve", c));
        if (Find("vms_train_info.json") is { } t) list.Add(("train_info", t));
        if (File.Exists(localLog)) list.Add(("log", localLog));
        return list;
    }

    /// <summary>작업 폴더는 디버그용으로 보존 후 JobRetentionHours 뒤 정리</summary>
    private void CleanupOldJobs()
    {
        try
        {
            var cut = DateTime.UtcNow.AddHours(-_o.JobRetentionHours);
            foreach (var d in Directory.GetDirectories(_o.JobsDir))
                if (Directory.GetLastWriteTimeUtc(d) < cut) Directory.Delete(d, true);
        }
        catch (Exception ex) { logger.LogDebug(ex, "작업 폴더 정리 실패"); }
    }

    public sealed class PrepareException(string message, Exception? inner = null) : Exception(message, inner);
    public sealed class UploadException(string message, Exception? inner = null) : Exception(message, inner);
}

/// <summary>
/// 진행률·로그를 1초 단위로 코얼레싱해 서버에 보고한다. 서버가 취소를 알리면 cancelJob 을 발동한다.
/// stdout·stderr 리더가 동시에 로그를 넣으므로, 보고 자체는 <see cref="_sending"/> 로 한 번에 하나만 나가게 한다.
/// (동시에 두 번 나가면 서버가 같은 LogSeq 를 두 번 쓰려다 실패한다.)
/// </summary>
public sealed class ProgressReporter(ServerClient server, Guid jobId, ILogger logger, CancellationTokenSource cancelJob)
{
    private readonly object _lock = new();
    private readonly SemaphoreSlim _sending = new(1, 1);
    private readonly List<LogChunkDto> _pending = new();
    private DateTime _lastFlush = DateTime.MinValue;
    private double _progress;
    private int _epoch, _total;
    private double? _loss, _metric;
    private bool _dirty;

    public void Update(TrainingLineEvent ev)
    {
        lock (_lock)
        {
            _progress = ev.Progress; _epoch = ev.CurrentEpoch; _total = ev.TotalEpochs;
            if (ev.Kind == TrainingOutputKind.Loss) _loss = ev.Loss;
            if (ev.Kind == TrainingOutputKind.Accuracy) _metric = ev.Metric;
            _dirty = true;
        }
    }

    public Task LogAsync(JobLogLevel level, string text, CancellationToken ct)
    {
        lock (_lock)
        {
            _pending.Add(new LogChunkDto(level, text, DateTime.UtcNow));
            _dirty = true;
        }
        return MaybeFlushAsync(ct);
    }

    public Task MaybeFlushAsync(CancellationToken ct) =>
        (DateTime.UtcNow - _lastFlush) >= TimeSpan.FromSeconds(1) ? FlushAsync(null, ct) : Task.CompletedTask;

    public async Task FlushAsync(TrainingJobState? state, CancellationToken ct)
    {
        // 보고는 한 번에 하나만. 이미 나가는 중이면 이번 호출은 접고, 쌓인 내용은 다음 보고에 실린다.
        if (!await _sending.WaitAsync(state is null ? 0 : Timeout.Infinite, CancellationToken.None)) return;
        try
        {
            ProgressReport report;
            lock (_lock)
            {
                if (!_dirty && state is null) return;
                var chunks = _pending.Count == 0 ? null : _pending.ToList();
                _pending.Clear();
                _dirty = false;
                _lastFlush = DateTime.UtcNow;
                report = new ProgressReport(state, _progress, _epoch, _total, _loss, _metric, chunks);
            }
            try
            {
                if (await server.ProgressAsync(jobId, report, ct) && !cancelJob.IsCancellationRequested)
                {
                    logger.LogWarning("서버 취소 요청 수신 — 작업 {Job}", jobId);
                    cancelJob.Cancel();
                }
            }
            catch (ServerApiException ex) when (ex.IsClientError)
            {
                // 서버가 작업을 재큐/취소했으면(409/403) 로컬 실행을 중단한다
                logger.LogWarning(ex, "progress 거부 → 작업 중단");
                cancelJob.Cancel();
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "progress 보고 실패 (다음 주기에 재시도)");
                lock (_lock) { _dirty = true; if (report.LogChunks is not null) _pending.InsertRange(0, report.LogChunks); }
            }
        }
        finally
        {
            _sending.Release();
        }
    }
}
