using BODA.VMS.MLOps.TrainWorker;
using FluentAssertions;
using Microsoft.Extensions.Configuration;

namespace BODA.VMS.MLOps.Tests.Worker;

/// <summary>
/// 워커 설정의 출처와 순서 (ML-32): 명령줄 &gt; 환경변수 &gt; 설정 파일 &gt; appsettings, 설정 파일은 --config 로 고른다.
/// 예전에는 worker.json 이 가장 세서 한 PC 에 두 번째 워커를 띄울 수 없었다.
/// 아무것도 주지 않으면 예전과 똑같이 기본 worker.json 을 읽어야 한다 — MSI 가 설치한 서비스는 인자 없이 돈다.
/// </summary>
public class WorkerConfigurationTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "mlops-workercfg-" + Guid.NewGuid().ToString("N")[..8]);

    public WorkerConfigurationTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private string WriteConfig(string name, string server, string cacheRoot)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllText(path, $$"""
            { "Worker": { "ServerUrl": "{{server}}", "Name": "{{name}}", "CacheRoot": "{{cacheRoot.Replace("\\", "\\\\")}}", "Token": "wk_file" } }
            """);
        return path;
    }

    private static WorkerOptions Bind(string configPath, string[] args, Dictionary<string, string?>? env = null,
        Dictionary<string, string?>? appsettings = null)
    {
        var builder = new ConfigurationBuilder().AddInMemoryCollection(appsettings ?? []);
        var cfg = WorkerConfiguration.Layer(builder, configPath, args, env ?? []).Build();
        var o = new WorkerOptions();
        cfg.GetSection(WorkerOptions.Section).Bind(o);
        return o;
    }

    [Fact]
    public void Installed_service_without_arguments_reads_the_default_worker_json_as_before()
    {
        WorkerConfiguration.ResolveConfigPath([], _ => null).Should().Be(WorkerOptions.ConfigFilePath);

        // 설치된 서비스 모양: appsettings 기본값 위에 worker.json, 인자·환경변수 없음 → 파일 값이 그대로 쓰인다
        var file = WriteConfig("worker.json", "http://server:5310", @"D:\cache1");
        var o = Bind(file, [], appsettings: new() { ["Worker:ServerUrl"] = "http://localhost:5310", ["Worker:RetryDelaySec"] = "7" });
        o.ServerUrl.Should().Be("http://server:5310", "설정 파일은 appsettings 를 이긴다");
        o.Name.Should().Be("worker.json");
        o.Token.Should().Be("wk_file");
        o.RetryDelaySec.Should().Be(7, "파일에 없는 값은 appsettings 를 그대로 쓴다");
    }

    [Fact]
    public void Command_line_beats_environment_beats_config_file()
    {
        var file = WriteConfig("worker.json", "http://server:5310", @"D:\cache1");

        var env = new Dictionary<string, string?> { ["Worker__Name"] = "env-name", ["Worker__CacheRoot"] = @"D:\cache-env" };
        var fromEnv = Bind(file, [], env);
        fromEnv.Name.Should().Be("env-name", "환경변수는 설정 파일을 이긴다 — 예전에는 거꾸로였다");
        fromEnv.CacheRoot.Should().Be(@"D:\cache-env");
        fromEnv.ServerUrl.Should().Be("http://server:5310");

        var fromArgs = Bind(file, ["--Worker:Name", "arg-name"], env);
        fromArgs.Name.Should().Be("arg-name", "명령줄은 환경변수도 이긴다");
        fromArgs.CacheRoot.Should().Be(@"D:\cache-env");
    }

    [Fact]
    public void Second_worker_uses_its_own_config_file()
    {
        WriteConfig("worker.json", "http://server:5310", @"D:\cache1");
        var second = WriteConfig("worker2.json", "http://server:5310", @"D:\cache2");

        string[] args = ["--config", second];
        var path = WorkerConfiguration.ResolveConfigPath(args, _ => null);
        path.Should().Be(Path.GetFullPath(second));

        var o = Bind(path, args);
        o.Name.Should().Be("worker2.json");
        o.CacheRoot.Should().Be(@"D:\cache2", "두 번째 워커는 캐시·이름이 첫 워커와 섞이지 않는다");

        WorkerConfiguration.WithoutConfigArg(["configure", "--config", second, "--name", "GPU-02"])
            .Should().Equal("configure", "--name", "GPU-02");
        WorkerConfiguration.ResolveConfigPath([$"--config={second}"], _ => null).Should().Be(Path.GetFullPath(second));
    }

    [Fact]
    public void Environment_variable_picks_the_config_file_when_no_argument()
    {
        var second = WriteConfig("worker-env.json", "http://s:1", @"D:\c");
        WorkerConfiguration.ResolveConfigPath([], key => key == WorkerConfiguration.ConfigEnvVar ? second : null)
            .Should().Be(Path.GetFullPath(second));
        // 명령줄이 이긴다
        var other = Path.Combine(_dir, "x.json");
        WorkerConfiguration.ResolveConfigPath(["--config", other], _ => second).Should().Be(Path.GetFullPath(other));
    }

    [Fact]
    public void Missing_path_after_config_is_an_error()
    {
        var act = () => WorkerConfiguration.ResolveConfigPath(["--config"], _ => null);
        act.Should().Throw<ArgumentException>();
    }
}
