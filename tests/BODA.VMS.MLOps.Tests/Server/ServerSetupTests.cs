using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json.Nodes;
using BODA.VMS.MLOps.Server;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;

namespace BODA.VMS.MLOps.Tests.Server;

/// <summary>
/// 설치 패키지가 부르는 <c>configure</c> 와, 서버가 그 결과를 읽는 자리.
///
/// <para>
/// 설치 뒤에는 이 파일 하나가 서버의 운영 설정입니다. 여기가 틀리면 서비스가 부팅을 거부하거나,
/// 더 나쁘게는 <b>조용히 틀린 값으로 뜹니다</b> (개발 서버가 운영 DB 를 쓰는 식).
/// </para>
/// </summary>
public class ServerSetupTests : IDisposable
{
    private const string Key = "0123456789abcdef0123456789abcdef-운영웹과같은키";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "mlops-setup-tests", Guid.NewGuid().ToString("N"));
    private readonly StringWriter _out = new();
    private readonly StringWriter _err = new();

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, true); } catch { }
        GC.SuppressFinalize(this);
    }

    private int Configure(params string[] args) => ServerSetup.Configure(args, _root, _out, _err);

    private JsonObject ReadConfig() =>
        (JsonObject)JsonNode.Parse(File.ReadAllText(ServerSetup.ConfigFilePath(_root)))!;

    private string DataDir(string name) => Path.Combine(_root, name);

    // ───────────── configure ─────────────

    [Fact]
    public void Writes_the_settings_the_service_needs()
    {
        var data = DataDir("data");

        Configure("--data-dir", data, "--port", "5400", "--web-url", "http://web:5292/", "--jwt-key", Key)
            .Should().Be(0, _err.ToString());

        var cfg = ReadConfig();
        cfg["Urls"]!.GetValue<string>().Should().Be("http://0.0.0.0:5400");
        cfg["ConnectionStrings"]!["DefaultConnection"]!.GetValue<string>()
            .Should().Be($"Data Source={data.Replace('\\', '/')}/mlops.db");
        cfg["Mlops"]!["StorageRoot"]!.GetValue<string>().Should().Be($"{data.Replace('\\', '/')}/storage");
        cfg["Monitoring"]!["ProductionWebUrl"]!.GetValue<string>().Should().Be("http://web:5292", "끝 / 는 떼어 낸다");
        cfg["Auth"]!["EnableDevTokens"]!.GetValue<bool>().Should().BeFalse("운영에서 개발 토큰은 꺼져 있어야 한다");
        cfg["Jwt"]!["Audience"]!.GetValue<string>().Should().Be("BODA.VMS.Web.Client");

        Directory.Exists(Path.Combine(data, "storage")).Should().BeTrue();
        Directory.Exists(Path.Combine(data, "logs")).Should().BeTrue();
    }

    /// <summary>
    /// 이 키로 MLOps 와 운영 웹 양쪽의 Admin 토큰을 만들 수 있다. 파일에 평문으로 남으면 안 된다.
    /// </summary>
    [Fact]
    public void The_key_is_not_stored_in_plain_text()
    {
        Configure("--data-dir", DataDir("d"), "--jwt-key", Key).Should().Be(0, _err.ToString());

        var text = File.ReadAllText(ServerSetup.ConfigFilePath(_root));
        text.Should().NotContain(Key);

        var stored = ReadConfig()["Jwt"]!["Key"]!.GetValue<string>();
        stored.Should().StartWith(ServerSetup.DpapiPrefix);
        ServerSetup.UnprotectKey(stored).Should().Be(Key);
    }

    /// <summary>
    /// 같은 PC 의 일반 사용자도 읽지 못해야 한다. DPAPI(LocalMachine)는 같은 PC 의 누구든 풀 수 있으므로
    /// 파일 권한이 실제 방어선이다.
    /// </summary>
    [Fact]
    public void Ordinary_users_cannot_read_the_settings_file()
    {
        Configure("--data-dir", DataDir("d"), "--jwt-key", Key).Should().Be(0, _err.ToString());

        var acl = new FileInfo(ServerSetup.ConfigFilePath(_root)).GetAccessControl();
        acl.AreAccessRulesProtected.Should().BeTrue("부모 폴더의 Users 읽기 권한이 내려오면 안 된다");

        var users = new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null);
        var authenticated = new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null);
        var everyone = new SecurityIdentifier(WellKnownSidType.WorldSid, null);
        acl.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>()
            .Select(r => (SecurityIdentifier)r.IdentityReference)
            .Should().NotContain([users, authenticated, everyone]);
    }

    /// <summary>
    /// 업그레이드 때 사람이 키를 다시 넣지 않아도 된다. 비워 두면 있던 키를 그대로 둔다 —
    /// 그러지 않으면 업그레이드 한 번에 서버가 부팅을 거부한다.
    /// </summary>
    [Fact]
    public void Reconfiguring_without_a_key_keeps_the_one_already_there()
    {
        Configure("--data-dir", DataDir("d"), "--port", "5310", "--jwt-key", Key).Should().Be(0);
        var before = ReadConfig()["Jwt"]!["Key"]!.GetValue<string>();

        Configure("--port", "5311").Should().Be(0, _err.ToString());

        var cfg = ReadConfig();
        cfg["Jwt"]!["Key"]!.GetValue<string>().Should().Be(before);
        cfg["Urls"]!.GetValue<string>().Should().Be("http://0.0.0.0:5311");
        // 주지 않은 값은 그대로다 — 기본값으로 되돌아가면 데이터 폴더가 조용히 바뀐다
        cfg["Mlops"]!["DataDir"]!.GetValue<string>().Should().Be(Path.GetFullPath(DataDir("d")));
    }

    /// <summary>첫 설치에 키가 없으면 파일을 만들지 않는다 — 키 없는 설정으로 서비스가 뜨려다 죽는 것보다 낫다.</summary>
    [Fact]
    public void A_first_install_without_a_key_is_refused()
    {
        Configure("--data-dir", DataDir("d")).Should().Be(2);
        _err.ToString().Should().Contain("--jwt-key");
        File.Exists(ServerSetup.ConfigFilePath(_root)).Should().BeFalse();
    }

    [Theory]
    [InlineData("--jwt-key", "짧은키")]
    [InlineData("--port", "0")]
    [InlineData("--port", "70000")]
    [InlineData("--port", "숫자아님")]
    [InlineData("--web-url", "ftp://web")]
    [InlineData("--web-url", "web:5292")]
    public void Bad_input_is_refused_before_anything_is_written(string option, string value)
    {
        var args = new List<string> { "--data-dir", DataDir("d") };
        if (option != "--jwt-key") args.AddRange(["--jwt-key", Key]);
        args.AddRange([option, value]);

        Configure(args.ToArray()).Should().Be(2);
        File.Exists(ServerSetup.ConfigFilePath(_root)).Should().BeFalse();
    }

    /// <summary>운영 웹 주소를 비우는 것은 허용한다 — 모니터링만 꺼진다.</summary>
    [Fact]
    public void An_empty_web_url_turns_monitoring_off_but_is_accepted()
    {
        Configure("--data-dir", DataDir("d"), "--jwt-key", Key, "--web-url", "").Should().Be(0, _err.ToString());
        ReadConfig()["Monitoring"]!["ProductionWebUrl"]!.GetValue<string>().Should().BeEmpty();
    }

    // ───────────── 서버가 읽는 자리 ─────────────

    private sealed class Env(string name) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;
        public string ApplicationName { get; set; } = "test";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    /// <summary>
    /// WebApplication.CreateBuilder 가 꾸리는 원천 순서를 그대로 흉내 낸다 —
    /// appsettings.json → appsettings.{환경}.json → (환경변수) → 명령줄.
    /// Apply 가 server.json 을 어디에 끼우는지는 이 순서 안에서만 뜻이 있다.
    /// </summary>
    private ConfigurationManager Builder(string environment, string? environmentJson = null, params string[] args)
    {
        var contentRoot = Path.Combine(_root, "content");
        Directory.CreateDirectory(contentRoot);
        File.WriteAllText(Path.Combine(contentRoot, "appsettings.json"), """{ "Urls": "http://from-appsettings:1" }""");
        var envFile = Path.Combine(contentRoot, $"appsettings.{environment}.json");
        if (environmentJson is not null) File.WriteAllText(envFile, environmentJson);

        var config = new ConfigurationManager();
        config.AddJsonFile(Path.Combine(contentRoot, "appsettings.json"), optional: true);
        config.AddJsonFile(envFile, optional: true);
        config.AddCommandLine(args);
        return config;
    }

    [Fact]
    public void Production_reads_the_file_and_unwraps_the_key()
    {
        Configure("--data-dir", DataDir("d"), "--port", "5400", "--jwt-key", Key).Should().Be(0);

        var config = Builder(Environments.Production);
        ServerSetup.Apply(config, new Env(Environments.Production), _root);

        config["Jwt:Key"].Should().Be(Key, "서버는 풀린 키를 봐야 한다");
        config["Urls"].Should().Be("http://0.0.0.0:5400", "server.json 이 appsettings.json 을 이긴다");
    }

    /// <summary>
    /// 개발 PC 가 서비스를 겸하면 ProgramData 에 이 파일이 있다. <c>dotnet run</c> 이 그것을 집어 가면
    /// 개발 서버가 조용히 운영 DB·운영 포트를 쓰게 된다.
    /// </summary>
    [Fact]
    public void Development_ignores_the_installed_settings()
    {
        Configure("--data-dir", DataDir("d"), "--port", "5400", "--jwt-key", Key).Should().Be(0);

        var config = Builder(Environments.Development);
        ServerSetup.Apply(config, new Env(Environments.Development), _root);

        config["Urls"].Should().Be("http://from-appsettings:1");
        config["Jwt:Key"].Should().BeNull();
    }

    /// <summary>
    /// 스크립트로 깐 PC 에 지난 MSI 설치가 남긴 server.json 이 있어도(제거해도 남기는 파일이다)
    /// 스크립트의 appsettings.Production.json 이 이겨야 한다. 그러지 않으면 스크립트 서비스가
    /// 다음 재시작 때 조용히 다른 DB·포트로 뜬다.
    /// </summary>
    [Fact]
    public void A_leftover_file_does_not_override_the_script_installed_settings()
    {
        Configure("--data-dir", DataDir("old-msi-data"), "--port", "5400", "--jwt-key", Key).Should().Be(0);

        var config = Builder(Environments.Production,
            """{ "Urls": "http://0.0.0.0:5310", "ConnectionStrings": { "DefaultConnection": "Data Source=D:/script/mlops.db" } }""");
        ServerSetup.Apply(config, new Env(Environments.Production), _root);

        config["Urls"].Should().Be("http://0.0.0.0:5310");
        config["ConnectionStrings:DefaultConnection"].Should().Be("Data Source=D:/script/mlops.db");
    }

    /// <summary>운영자가 명령줄(과 환경변수)로 한 값을 덮을 수 있어야 한다 — 파일이 그것을 이기면 안 된다.</summary>
    [Fact]
    public void The_command_line_still_wins_over_the_file()
    {
        Configure("--data-dir", DataDir("d"), "--web-url", "http://from-file:5292", "--jwt-key", Key).Should().Be(0);

        var config = Builder(Environments.Production, null, "--Monitoring:ProductionWebUrl=http://from-args:5292");
        ServerSetup.Apply(config, new Env(Environments.Production), _root);

        config["Monitoring:ProductionWebUrl"].Should().Be("http://from-args:5292");
    }

    /// <summary>
    /// 명령줄(또는 환경변수)로 평문 키를 주면 그것을 쓴다. 스크립트로 깐 서비스가 이 경우다 (환경변수 Jwt__Key).
    /// </summary>
    [Fact]
    public void A_plain_key_from_the_command_line_wins_over_the_protected_one()
    {
        Configure("--data-dir", DataDir("d"), "--jwt-key", Key).Should().Be(0);
        const string other = "fedcba9876543210fedcba9876543210-다른키";

        var config = Builder(Environments.Production, null, $"--Jwt:Key={other}");
        ServerSetup.Apply(config, new Env(Environments.Production), _root);

        config["Jwt:Key"].Should().Be(other);
    }
}
