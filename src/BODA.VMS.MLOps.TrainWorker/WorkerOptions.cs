using System.Security.Cryptography;
using System.Text;
using BODA.VMS.MLOps.Core.Domain;

namespace BODA.VMS.MLOps.TrainWorker;

/// <summary>
/// 워커 설정 — appsettings "Worker" 섹션 + %ProgramData%\BODA VMS TrainWorker\worker.json (설치 시 `configure` 로 생성, Phase 3 §10).
/// 토큰은 "dpapi:" 접두사면 DPAPI(LocalMachine) 로 복호화한다.
/// </summary>
public sealed class WorkerOptions
{
    public const string Section = "Worker";
    public const string DpapiPrefix = "dpapi:";
    public static readonly string DefaultRoot =
        Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.CommonApplicationData), "BODA VMS TrainWorker");
    public static string ConfigFilePath => Path.Combine(DefaultRoot, "worker.json");

    public string ServerUrl { get; set; } = "http://localhost:5310";
    public string Token { get; set; } = "";
    public string Name { get; set; } = System.Environment.MachineName;
    public string CacheRoot { get; set; } = "";
    /// <summary>지정하면 venv 부트스트랩을 건너뛰고 이 파이썬을 쓴다 (예: 기존 torch 환경)</summary>
    public string? PythonExe { get; set; }
    /// <summary>오프라인 wheel 번들 폴더 (pip --no-index --find-links)</summary>
    public string? WheelBundleDir { get; set; }
    /// <summary>venv 를 만들 기반 파이썬 (기본: py -3.12)</summary>
    public string BasePythonVersion { get; set; } = "3.12";
    public int GpuIndex { get; set; } = 0;
    public int MaxSilenceMinutes { get; set; } = 30;
    public int MaxDurationHours { get; set; } = 24;
    public int DatasetCacheLimitGB { get; set; } = 100;
    public int JobRetentionHours { get; set; } = 24;
    public TaskType[]? TaskTypes { get; set; }
    /// <summary>테스트·CPU 전용 환경: CUDA 없어도 Disabled 로 만들지 않는다</summary>
    public bool AllowCpuOnly { get; set; }
    /// <summary>자기진단 생략 (테스트 전용)</summary>
    public bool SkipDiagnostics { get; set; }
    public int RetryDelaySec { get; set; } = 10;

    public string ResolvedCacheRoot() => string.IsNullOrWhiteSpace(CacheRoot) ? DefaultRoot : Path.GetFullPath(CacheRoot);
    public string VenvDir => Path.Combine(ResolvedCacheRoot(), "venv");
    public string DatasetsDir => Path.Combine(ResolvedCacheRoot(), "datasets");
    public string PretrainedDir => Path.Combine(ResolvedCacheRoot(), "pretrained");
    public string JobsDir => Path.Combine(ResolvedCacheRoot(), "jobs");
    public string ScriptsDir => Path.Combine(ResolvedCacheRoot(), "scripts");
    public string LogsDir => Path.Combine(ResolvedCacheRoot(), "logs");
    /// <summary>설치 패키지에 동봉된 스크립트(진단·허용 목록·train_*.py 초기본)</summary>
    public static string BundledScriptsDir => Path.Combine(AppContext.BaseDirectory, "scripts");

    public string ResolveToken()
    {
        if (string.IsNullOrWhiteSpace(Token)) return "";
        if (!Token.StartsWith(DpapiPrefix, StringComparison.Ordinal)) return Token.Trim();
        if (!OperatingSystem.IsWindows()) throw new InvalidOperationException("DPAPI 토큰은 Windows 에서만 복호화할 수 있습니다.");
        var bytes = ProtectedData.Unprotect(Convert.FromBase64String(Token[DpapiPrefix.Length..]), null, DataProtectionScope.LocalMachine);
        return Encoding.UTF8.GetString(bytes);
    }

    public static string ProtectToken(string token)
    {
        if (!OperatingSystem.IsWindows()) return token;
        var bytes = ProtectedData.Protect(Encoding.UTF8.GetBytes(token), null, DataProtectionScope.LocalMachine);
        return DpapiPrefix + Convert.ToBase64String(bytes);
    }

    public void EnsureDirectories()
    {
        foreach (var d in new[] { ResolvedCacheRoot(), DatasetsDir, PretrainedDir, JobsDir, ScriptsDir, LogsDir })
            Directory.CreateDirectory(d);
    }

    public double DiskFreeGB()
    {
        try { return new DriveInfo(Path.GetPathRoot(ResolvedCacheRoot())!).AvailableFreeSpace / 1e9; }
        catch { return 0; }
    }
}
