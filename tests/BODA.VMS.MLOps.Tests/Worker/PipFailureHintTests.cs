using BODA.VMS.MLOps.TrainWorker.Environment;

namespace BODA.VMS.MLOps.Tests.Worker;

/// <summary>
/// 패키지 설치(pip) 실패 로그에 붙는 다음 할 일 (2026-11-22 매뉴얼 대조).
/// 폐쇄망 PC 에 MSI 만 실행하면 wheel 폴더가 지정되지 않아 pip 가 인터넷으로 나가고, 로그에는 pip 의 끝줄만 남아
/// 60초마다 같은 실패가 되풀이됐다 — 무엇을 해야 하는지를 같은 줄에 적는다.
/// </summary>
public class PipFailureHintTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void WithoutWheelFolder_PointsToOfflineInstall(string? wheels)
    {
        var hint = PythonEnvironment.PipFailureHint(wheels);

        Assert.Contains("install-offline.cmd", hint);
        Assert.Contains("--wheels", hint);
        // configure 는 설정을 통째로 다시 쓰므로 서버 주소와 토큰도 함께 줘야 한다
        Assert.Contains("--server", hint);
        Assert.Contains("--token", hint);
    }

    [Fact]
    public void WithWheelFolder_NamesTheFolderInsteadOfTheInternet()
    {
        var hint = PythonEnvironment.PipFailureHint(@"C:\ProgramData\BODA VMS TrainWorker\wheels");

        Assert.Contains(@"C:\ProgramData\BODA VMS TrainWorker\wheels", hint);
        Assert.DoesNotContain("install-offline.cmd", hint);
        Assert.DoesNotContain("인터넷(pip)", hint);
    }
}
