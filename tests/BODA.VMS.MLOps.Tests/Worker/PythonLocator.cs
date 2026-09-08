using System.Diagnostics;

namespace BODA.VMS.MLOps.Tests.Worker;

/// <summary>테스트 PC 의 파이썬(3.x) 탐색 — 없으면 워커 프로세스 테스트는 건너뛴다</summary>
public static class PythonLocator
{
    private static readonly Lazy<string?> Cached = new(Find);
    public static string? Path => Cached.Value;
    public static string FakeScript => System.IO.Path.Combine(AppContext.BaseDirectory, "scripts", "train_fake.py");

    private static string? Find()
    {
        foreach (var (exe, args) in new (string, string[])[] { ("py", ["-3", "-c", "import sys;print(sys.executable)"]), ("python", ["-c", "import sys;print(sys.executable)"]) })
        {
            try
            {
                var psi = new ProcessStartInfo(exe) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
                foreach (var a in args) psi.ArgumentList.Add(a);
                using var p = Process.Start(psi);
                if (p is null) continue;
                var output = p.StandardOutput.ReadToEnd().Trim();
                p.WaitForExit(10000);
                if (p.ExitCode == 0 && File.Exists(output)) return output;
            }
            catch { }
        }
        return null;
    }
}
