using System.Diagnostics;
using System.Text;
using System.Text.Json;
using BODA.VMS.MLOps.Contracts;
using BODA.VMS.MLOps.Contracts.Workers;
using BODA.VMS.MLOps.Core.Hashing;
using BODA.VMS.MLOps.TrainWorker.Agent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BODA.VMS.MLOps.TrainWorker.Environment;

/// <summary>
/// 파이썬 venv 부트스트랩 + 자기진단 (Phase 3 §6).
/// 첫 시작: py -3.12 탐색 → venv 생성 → 허용 목록 패키지 설치(온라인 pip 또는 오프라인 wheel 번들) → worker_diag.py 실행.
/// </summary>
public sealed class PythonEnvironment(IOptions<WorkerOptions> options, ILogger<PythonEnvironment> logger)
{
    private readonly WorkerOptions _o = options.Value;

    public string PythonExe => !string.IsNullOrWhiteSpace(_o.PythonExe) ? _o.PythonExe! : Path.Combine(_o.VenvDir, "Scripts", "python.exe");
    public static string DiagScript => Path.Combine(WorkerOptions.BundledScriptsDir, "worker_diag.py");
    public static string RequirementsFile => Path.Combine(WorkerOptions.BundledScriptsDir, "requirements-allowlist.txt");

    /// <summary>실행 가능한 파이썬 경로를 보장한다 (없으면 venv 부트스트랩)</summary>
    public async Task<string> EnsureAsync(CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(_o.PythonExe))
        {
            if (!File.Exists(_o.PythonExe)) throw new FileNotFoundException("Worker:PythonExe 가 없습니다.", _o.PythonExe);
            return _o.PythonExe;
        }
        // 완료 마커가 있어야 "만들어진 venv" 다. pip 도중 실패한 venv 를 완성품으로 믿으면 다음 재시도가 진단(torch import 실패)으로
        // 넘어가 Disabled 에 갇힌다 (2026-09-10 MSI 실증: 3.14 로 만든 venv 에 torch 설치 실패 → 반쪽 venv 잔류).
        var marker = Path.Combine(_o.VenvDir, ".complete");
        if (File.Exists(PythonExe) && File.Exists(marker)) return PythonExe;
        if (Directory.Exists(_o.VenvDir))
        {
            logger.LogWarning("완료되지 않은 venv 를 지우고 다시 만듭니다: {Dir}", _o.VenvDir);
            Directory.Delete(_o.VenvDir, recursive: true);
        }

        logger.LogInformation("venv 부트스트랩 시작: {Dir}", _o.VenvDir);
        var basePython = await FindBasePythonAsync(ct) ?? throw new InvalidOperationException(
            $"기반 파이썬 {_o.BasePythonVersion}(또는 3.11)을 찾을 수 없습니다. 서비스 계정은 사용자 전용 설치(HKCU)와 사용자 PATH 를 보지 못합니다 — " +
            "Python 3.12 를 \"모든 사용자용\" 으로 설치하거나 configure --python <python.exe> 로 지정하세요.");
        await RunAsync(basePython, ["-m", "venv", _o.VenvDir], null, ct, throwOnError: true);

        if (File.Exists(RequirementsFile))
        {
            var disallowed = PackageAllowlist.FindDisallowed(await File.ReadAllLinesAsync(RequirementsFile, ct));
            if (disallowed.Count > 0)
                throw new InvalidOperationException("허용 목록 밖 패키지가 requirements 에 있습니다: " + string.Join(", ", disallowed));

            var pip = new List<string> { "-m", "pip", "install", "--disable-pip-version-check" };
            if (!string.IsNullOrWhiteSpace(_o.WheelBundleDir))
                pip.AddRange(["--no-index", "--find-links", _o.WheelBundleDir]);
            pip.AddRange(["-r", RequirementsFile]);
            await RunAsync(PythonExe, pip, null, ct, throwOnError: true);
        }
        else
        {
            logger.LogWarning("requirements-allowlist.txt 가 없어 패키지 설치를 건너뜁니다: {Path}", RequirementsFile);
        }
        await File.WriteAllTextAsync(marker, $"{basePython}\n{DateTime.UtcNow:O}\n", ct);
        return PythonExe;
    }

    /// <summary>worker_diag.py 실행 → WorkerCapabilities. 실패 항목이 있으면 DiagnosticFailures 에 담긴다 (서버가 Disabled 처리).</summary>
    public async Task<WorkerCapabilities> DiagnoseAsync(string pythonExe, string scriptsHash, CancellationToken ct)
    {
        var failures = new List<string>();
        string? gpuName = null, cudaVersion = null, pyVersion = null;
        int gpuMem = 0;
        bool cuda = false;
        var packages = new Dictionary<string, string>();
        string[]? providers = null;

        if (_o.SkipDiagnostics)
        {
            pyVersion = "skipped";
            cuda = true;
        }
        else if (!File.Exists(DiagScript))
        {
            failures.Add($"진단 스크립트 없음: {DiagScript}");
        }
        else
        {
            var (code, stdout, stderr) = await RunAsync(pythonExe, [DiagScript], null, ct, throwOnError: false, timeout: TimeSpan.FromMinutes(5));
            if (code != 0)
                failures.Add($"진단 스크립트 종료 코드 {code}: {Tail(stderr)}");
            else
            {
                try
                {
                    using var doc = JsonDocument.Parse(stdout);
                    var r = doc.RootElement;
                    pyVersion = r.TryGetProperty("python_version", out var pv) ? pv.GetString() : null;
                    cuda = r.TryGetProperty("cuda_available", out var ca) && ca.ValueKind == JsonValueKind.True;
                    gpuName = r.TryGetProperty("gpu_name", out var gn) ? gn.GetString() : null;
                    gpuMem = r.TryGetProperty("gpu_mem_mb", out var gm) && gm.TryGetInt32(out var gmi) ? gmi : 0;
                    cudaVersion = r.TryGetProperty("cuda_version", out var cv) ? cv.GetString() : null;
                    if (r.TryGetProperty("packages", out var pk) && pk.ValueKind == JsonValueKind.Object)
                        foreach (var p in pk.EnumerateObject()) packages[p.Name] = p.Value.GetString() ?? "";
                    if (r.TryGetProperty("onnxruntime_providers", out var op) && op.ValueKind == JsonValueKind.Array)
                        providers = op.EnumerateArray().Select(x => x.GetString() ?? "").ToArray();
                    if (r.TryGetProperty("failures", out var fl) && fl.ValueKind == JsonValueKind.Array)
                        failures.AddRange(fl.EnumerateArray().Select(x => x.GetString() ?? "").Where(s => s.Length > 0));
                }
                catch (JsonException ex)
                {
                    failures.Add($"진단 결과 파싱 실패: {ex.Message} / {Tail(stdout)}");
                }
            }
            if (!cuda && !_o.AllowCpuOnly) failures.Add("torch.cuda.is_available() == False (Worker:AllowCpuOnly 로 CPU 전용 허용 가능)");
        }

        return new WorkerCapabilities(gpuName, gpuMem, cudaVersion, cuda, pyVersion, packages, scriptsHash,
            _o.DiskFreeGB(), ServerClient.WorkerVersion, providers, failures.Count == 0 ? null : failures.ToArray());
    }

    /// <summary>로컬 scripts\ 폴더의 train_*.py 해시를 합친 값 (서버 매니페스트 대조용 요약)</summary>
    public static string ScriptsHash(string dir)
    {
        if (!Directory.Exists(dir)) return "";
        var parts = Directory.GetFiles(dir, "train_*.py").OrderBy(f => f, StringComparer.Ordinal)
            .Select(f => Path.GetFileName(f) + ":" + Sha256Util.HashFile(f));
        return Sha256Util.HashString(string.Join("\n", parts));
    }

    private async Task<string?> FindBasePythonAsync(CancellationToken ct)
    {
        // 1) PEP 514 레지스트리 — 서비스(LocalSystem)는 사용자 PATH 를 보지 못하므로 py 런처보다 먼저 본다.
        //    HKLM = "모든 사용자" 설치, HKCU = 설치한 사용자 전용(서비스 계정에서는 대개 비어 있다).
        if (OperatingSystem.IsWindows())
        {
            foreach (var ver in new[] { _o.BasePythonVersion, "3.11" })
            foreach (var root in new[] { Microsoft.Win32.Registry.LocalMachine, Microsoft.Win32.Registry.CurrentUser })
            {
                try
                {
                    using var key = root.OpenSubKey($@"Software\Python\PythonCore\{ver}\InstallPath");
                    var exe = key?.GetValue("ExecutablePath") as string;
                    if (string.IsNullOrWhiteSpace(exe) && key?.GetValue(null) is string dir) exe = Path.Combine(dir, "python.exe");
                    if (!string.IsNullOrWhiteSpace(exe) && File.Exists(exe)) { logger.LogInformation("기반 파이썬(레지스트리 {Ver}): {Exe}", ver, exe); return exe; }
                }
                catch (Exception ex) { logger.LogDebug(ex, "레지스트리 파이썬 탐색 실패 {Ver}", ver); }
            }
        }

        // 2) py 런처 / PATH 의 python — 버전을 확인한다. PATH 의 python 이 3.14 같은 다른 버전이면 venv 는 만들어지지만
        //    torch wheel 이 없어 pip 에서 "No matching distribution for torch" 로 실패한다 (2026-09-10 MSI 실증). 그 오류보다 여기서 막는 편이 읽기 쉽다.
        const string probe = "import sys;print(sys.executable);print('%d.%d' % sys.version_info[:2])";
        var accepted = new[] { _o.BasePythonVersion, "3.11" };
        var rejected = new List<string>();
        foreach (var (exe, args) in new (string, string[])[]
                 {
                     ("py", [$"-{_o.BasePythonVersion}", "-c", probe]),
                     ("py", ["-3.11", "-c", probe]),
                     ("python", ["-c", probe]),
                 })
        {
            try
            {
                var (code, stdout, _) = await RunAsync(exe, args, null, ct, throwOnError: false, timeout: TimeSpan.FromSeconds(20));
                var lines = stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (code != 0 || lines.Length < 2 || !File.Exists(lines[0])) continue;
                if (accepted.Contains(lines[1])) return lines[0];
                rejected.Add($"{lines[0]} ({lines[1]})");
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { logger.LogDebug(ex, "{Exe} 탐색 실패", exe); }
        }
        if (rejected.Count > 0)
            logger.LogWarning("버전이 맞지 않는 파이썬은 건너뜁니다 (필요: {Want}): {Found}", string.Join("/", accepted), string.Join(", ", rejected));
        return null;
    }

    public async Task<(int ExitCode, string Stdout, string Stderr)> RunAsync(string exe, IReadOnlyList<string> args, string? workDir,
        CancellationToken ct, bool throwOnError, TimeSpan? timeout = null)
    {
        var psi = new ProcessStartInfo
        {
            FileName = exe, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8, WorkingDirectory = workDir ?? System.Environment.CurrentDirectory,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        psi.Environment["PYTHONIOENCODING"] = "utf-8";
        psi.Environment["PYTHONUTF8"] = "1";

        logger.LogInformation("실행: {Exe} {Args}", exe, string.Join(' ', args.Select(a => a.Contains(' ') ? $"\"{a}\"" : a)));
        using var p = Process.Start(psi) ?? throw new InvalidOperationException($"프로세스 시작 실패: {exe}");
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (timeout is not null) cts.CancelAfter(timeout.Value);
        var outTask = p.StandardOutput.ReadToEndAsync(cts.Token);
        var errTask = p.StandardError.ReadToEndAsync(cts.Token);
        try
        {
            await p.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            try { p.Kill(true); } catch { }
            // 리더 태스크를 정리하지 않고 나가면 Process 가 dispose 된 뒤 fault 가 관측되지 않은 채로 남는다
            await Task.WhenAll(outTask, errTask).ContinueWith(_ => { }, TaskScheduler.Default);
            throw;
        }
        var stdout = await outTask;
        var stderr = await errTask;
        if (throwOnError && p.ExitCode != 0)
            throw new InvalidOperationException($"{exe} {args.FirstOrDefault()} 실패 (exit {p.ExitCode}): {Tail(stderr)}");
        return (p.ExitCode, stdout, stderr);
    }

    private static string Tail(string s, int max = 800) => s.Length <= max ? s.Trim() : "…" + s[^max..].Trim();
}
