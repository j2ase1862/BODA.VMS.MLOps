using BODA.VMS.MLOps.TrainWorker.Environment;

namespace BODA.VMS.MLOps.Tests.Worker;

/// <summary>
/// venv 완료 마커의 requirements 해시 — 허용 목록이 바뀌면 기존 venv 를 다시 맞춘다.
/// 종전 마커는 기반 파이썬 · 시각만 적어, 새 MSI 로 허용 목록이 바뀌어도 기존 워커가 옛 패키지로 남았다 (2026-10-14).
/// </summary>
public class VenvRequirementsRefreshTests
{
    [Fact]
    public void SameHash_NoRefresh()
    {
        var marker = PythonEnvironment.MarkerText(@"C:\Python312\python.exe", "abc123");
        Assert.False(PythonEnvironment.NeedsRequirementsRefresh(marker, "abc123"));
        Assert.False(PythonEnvironment.NeedsRequirementsRefresh(marker, "ABC123"));   // 대소문자 무관
    }

    [Fact]
    public void DifferentHash_Refresh()
    {
        var marker = PythonEnvironment.MarkerText(@"C:\Python312\python.exe", "abc123");
        Assert.True(PythonEnvironment.NeedsRequirementsRefresh(marker, "def456"));
    }

    [Fact]
    public void LegacyMarkerWithoutHash_RefreshOnce()
    {
        // 0.1.x 워커가 쓴 마커 — 기반 파이썬과 시각 두 줄뿐
        var legacy = "C:\\Python312\\python.exe\n2026-09-10T03:14:00.0000000Z\n";
        Assert.True(PythonEnvironment.NeedsRequirementsRefresh(legacy, "abc123"));
    }

    [Fact]
    public void MarkerWithoutRequirementsFile_HasNoHashLine()
    {
        var marker = PythonEnvironment.MarkerText(@"C:\Python312\python.exe", null);
        Assert.DoesNotContain("requirements-sha256=", marker);
        Assert.StartsWith(@"C:\Python312\python.exe", marker);
    }

    [Fact]
    public void CrlfMarker_IsParsed()
    {
        var marker = PythonEnvironment.MarkerText("py", "abc123").Replace("\n", "\r\n");
        Assert.False(PythonEnvironment.NeedsRequirementsRefresh(marker, "abc123"));
    }
}
