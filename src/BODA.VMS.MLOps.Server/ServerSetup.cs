using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration.Json;
using Microsoft.Extensions.FileProviders;

namespace BODA.VMS.MLOps.Server;

/// <summary>
/// 설치 뒤의 기계 설정 — <c>%ProgramData%\BODA VMS MLOps\server.json</c>.
///
/// <para>
/// 설치 패키지(MSI)가 <c>BODA.VMS.MLOps.Server.exe configure</c> 로 이 파일을 쓰고, 서버는 운영
/// 환경으로 뜰 때 이것을 설정에 끼웁니다 (자리는 <see cref="Apply"/> 참고). 워커의 <c>worker.json</c> 과 같은 모양입니다.
/// </para>
/// <para><b>왜 설치 폴더가 아니라 ProgramData 인가.</b>
/// MSI 는 업그레이드할 때 이전 판을 지우고 새로 깝니다. 설정을 설치 폴더나 서비스 레지스트리에 두면
/// 그때 함께 사라지고, 사람이 Jwt 키를 다시 넣지 않으면 서버가 부팅을 거부합니다.
/// ProgramData 의 이 파일은 MSI 가 모르는 파일이라 업그레이드·제거를 지나도 남습니다 — 재설치가 곧 복구입니다.
/// </para>
/// <para><b>Jwt 키.</b>
/// 이 키를 가진 사람은 MLOps 와 운영 웹 <b>양쪽의</b> Admin 토큰을 만들 수 있습니다(두 서버가 같은 키를 씁니다).
/// 그래서 DPAPI(LocalMachine)로 감싸 다른 PC 로 파일을 옮겨도 쓸 수 없게 하고, 파일 권한을
/// SYSTEM·Administrators 로 좁혀 같은 PC 의 일반 사용자도 읽지 못하게 합니다 —
/// DPAPI LocalMachine 은 같은 PC 의 누구든 풀 수 있으므로 권한이 실제 방어선입니다.
/// </para>
/// </summary>
public static class ServerSetup
{
    public const string DpapiPrefix = "dpapi:";

    /// <summary>설정 파일이 사는 곳. 데이터 폴더를 다른 드라이브로 옮겨도 이 자리는 그대로입니다.</summary>
    public static readonly string DefaultRoot = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "BODA VMS MLOps");

    public static string ConfigFilePath(string? root = null) => Path.Combine(root ?? DefaultRoot, "server.json");

    public const int DefaultPort = 5310;
    public const int MinKeyLength = 32;

    /// <summary>
    /// <c>configure --data-dir &lt;폴더&gt; --port &lt;n&gt; --web-url &lt;url&gt; --jwt-key &lt;키&gt;</c>
    ///
    /// <para>
    /// 있던 설정에 <b>덮어 합칩니다</b>. 준 값만 바뀝니다 — 특히 <c>--jwt-key</c> 를 비우면 있던 키를 그대로 둡니다.
    /// 업그레이드 때 사람이 키를 다시 넣지 않아도 되게 하려는 것입니다.
    /// </para>
    /// </summary>
    /// <returns>0 성공 · 2 잘못된 입력</returns>
    public static int Configure(string[] args, string? root = null, TextWriter? output = null, TextWriter? error = null)
    {
        output ??= Console.Out;
        error ??= Console.Error;
        root ??= DefaultRoot;

        string? Arg(string name)
        {
            for (var i = 0; i < args.Length - 1; i++)
                if (args[i].Equals(name, StringComparison.OrdinalIgnoreCase)) return args[i + 1];
            return null;
        }

        var configPath = ConfigFilePath(root);
        var existing = File.Exists(configPath)
            ? JsonNode.Parse(File.ReadAllText(configPath)) as JsonObject ?? new JsonObject()
            : new JsonObject();

        // ── 입력 확인 ──
        var dataDirArg = Arg("--data-dir");
        var dataDir = !string.IsNullOrWhiteSpace(dataDirArg)
            ? Path.GetFullPath(dataDirArg.Trim())
            : ReadString(existing, "Mlops", "DataDir") ?? root;

        var portArg = Arg("--port");
        int port;
        if (!string.IsNullOrWhiteSpace(portArg))
        {
            if (!int.TryParse(portArg.Trim(), out port) || port is < 1 or > 65535)
            {
                error.WriteLine($"--port 는 1~65535 여야 합니다: {portArg}");
                return 2;
            }
        }
        else
        {
            port = ReadInt(existing, "Mlops", "Port") ?? DefaultPort;
        }

        var webUrlArg = Arg("--web-url");
        string webUrl;
        if (webUrlArg is not null)
        {
            webUrl = webUrlArg.Trim().TrimEnd('/');
            // 비우는 것은 허용한다 — 모니터링만 꺼진다
            if (webUrl.Length > 0 && (!Uri.TryCreate(webUrl, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")))
            {
                error.WriteLine($"--web-url 은 http(s):// 주소여야 합니다: {webUrlArg}");
                return 2;
            }
        }
        else
        {
            webUrl = ReadString(existing, "Monitoring", "ProductionWebUrl") ?? "http://localhost:5292";
        }

        var keyArg = Arg("--jwt-key")?.Trim();
        string protectedKey;
        if (!string.IsNullOrEmpty(keyArg))
        {
            if (keyArg.Length < MinKeyLength)
            {
                error.WriteLine($"--jwt-key 는 {MinKeyLength}자 이상이어야 합니다 (운영 웹과 같은 키).");
                return 2;
            }
            protectedKey = ProtectKey(keyArg);
        }
        else if (ReadString(existing, "Jwt", "Key") is { Length: > 0 } kept)
        {
            protectedKey = kept;   // 업그레이드 — 있던 키를 그대로 둔다
        }
        else
        {
            error.WriteLine("Jwt 키가 없습니다. --jwt-key 로 운영 웹(BODA.VMS.Web)과 같은 키를 주세요.");
            return 2;
        }

        // ── 폴더 ──
        Directory.CreateDirectory(root);
        foreach (var dir in new[] { dataDir, Path.Combine(dataDir, "storage"), Path.Combine(dataDir, "logs") })
            Directory.CreateDirectory(dir);

        // ── 설정 쓰기 ── (경로는 / 로 적는다: SQLite 연결 문자열과 Serilog 가 둘 다 받아 준다)
        var slashDir = dataDir.Replace('\\', '/');
        var config = new JsonObject
        {
            ["_comment"] = "BODA VMS MLOps 서버 설치 설정. 설치 패키지가 configure 로 쓴다. 손으로 고쳤다면 서비스 BodaVmsMlops 를 다시 시작하세요.",
            ["Urls"] = $"http://0.0.0.0:{port}",
            ["ConnectionStrings"] = new JsonObject { ["DefaultConnection"] = $"Data Source={slashDir}/mlops.db" },
            ["Jwt"] = new JsonObject
            {
                ["Key"] = protectedKey,
                ["Issuer"] = "BODA.VMS.Web",
                ["Audience"] = "BODA.VMS.Web.Client",
            },
            ["Auth"] = new JsonObject { ["EnableDevTokens"] = false },
            ["Mlops"] = new JsonObject
            {
                ["DataDir"] = dataDir,
                ["Port"] = port,
                ["StorageRoot"] = $"{slashDir}/storage",
            },
            ["Monitoring"] = new JsonObject { ["ProductionWebUrl"] = webUrl },
            ["Serilog"] = new JsonObject
            {
                ["Using"] = new JsonArray("Serilog.Sinks.File"),
                ["WriteTo"] = new JsonArray(new JsonObject
                {
                    ["Name"] = "File",
                    ["Args"] = new JsonObject
                    {
                        ["path"] = $"{slashDir}/logs/server-.log",
                        ["rollingInterval"] = "Day",
                        ["retainedFileCountLimit"] = 30,
                        ["outputTemplate"] = "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}",
                    },
                }),
            },
        };

        var temp = configPath + ".tmp";
        File.WriteAllText(temp, config.ToJsonString(new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        }), new UTF8Encoding(false));
        RestrictToAdministrators(temp);
        File.Move(temp, configPath, overwrite: true);

        output.WriteLine($"설정: {configPath}");
        output.WriteLine($"  데이터 폴더 : {dataDir}");
        output.WriteLine($"  포트        : {port}");
        output.WriteLine($"  운영 웹     : {(webUrl.Length > 0 ? webUrl : "(비움 - 모니터링 꺼짐)")}");
        output.WriteLine($"  Jwt 키      : {(string.IsNullOrEmpty(keyArg) ? "있던 키 유지" : "새로 저장 (DPAPI)")}");
        return 0;
    }

    /// <summary>
    /// 운영으로 뜰 때 <c>server.json</c> 을 설정에 끼우고, DPAPI 로 감싼 키를 풀어 둔다.
    ///
    /// <para>
    /// <b>운영 환경일 때만 끼웁니다.</b> 개발 PC 가 서비스를 겸하면 ProgramData 에 이 파일이 있는데,
    /// <c>dotnet run</c> 이 그것을 집어 가면 개발 서버가 조용히 운영 DB·운영 포트를 쓰게 됩니다.
    /// </para>
    /// <para><b>자리는 <c>appsettings.Production.json</c> 바로 아래입니다.</b>
    /// 우선순위: appsettings.json &lt; server.json &lt; appsettings.Production.json &lt; 환경변수 &lt; 명령줄.
    /// 설치 패키지는 appsettings.Production.json 을 싣지 않으므로 MSI 로 깐 PC 에서는 server.json 이 곧 운영 설정입니다.
    /// 반대로 스크립트(install-server-service.ps1)로 깐 PC 는 appsettings.Production.json 을 쓰는데, 거기에
    /// 지난 MSI 설치가 남긴 server.json 이 있어도(제거해도 남기는 파일입니다) 스크립트의 설정이 이깁니다 —
    /// 그러지 않으면 스크립트 서비스가 다음 재시작 때 조용히 다른 DB·포트로 뜹니다.
    /// 환경변수·명령줄은 둘 다를 이깁니다. 운영자가 한 값을 덮을 수 있어야 합니다.
    /// </para>
    /// </summary>
    public static void Apply(ConfigurationManager configuration, IHostEnvironment environment, string? root = null)
    {
        if (environment.IsProduction())
        {
            var path = ConfigFilePath(root);
            if (File.Exists(path))
            {
                var source = new JsonConfigurationSource
                {
                    FileProvider = new PhysicalFileProvider(Path.GetDirectoryName(path)!),
                    Path = Path.GetFileName(path),
                    Optional = true,
                    ReloadOnChange = false,
                };
                IConfigurationBuilder builder = configuration;
                builder.Sources.Insert(IndexOfEnvironmentFile(builder.Sources, environment.EnvironmentName), source);
            }
        }

        if (configuration["Jwt:Key"] is { } key && key.StartsWith(DpapiPrefix, StringComparison.Ordinal))
            configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["Jwt:Key"] = UnprotectKey(key) });
    }

    /// <summary>
    /// <c>appsettings.{환경}.json</c> 원천의 자리. 그 앞에 끼우면 그것보다 약하고 appsettings.json 보다 강하다.
    /// 못 찾으면(시험처럼 원천을 손으로 꾸린 경우) 맨 앞 — 가장 약한 자리 — 에 둔다.
    /// </summary>
    private static int IndexOfEnvironmentFile(IList<IConfigurationSource> sources, string environmentName)
    {
        var name = $"appsettings.{environmentName}.json";
        for (var i = 0; i < sources.Count; i++)
            if (sources[i] is FileConfigurationSource f && string.Equals(f.Path, name, StringComparison.OrdinalIgnoreCase))
                return i;
        return 0;
    }

    public static string ProtectKey(string key)
    {
        if (!OperatingSystem.IsWindows()) return key;
        var bytes = ProtectedData.Protect(Encoding.UTF8.GetBytes(key), null, DataProtectionScope.LocalMachine);
        return DpapiPrefix + Convert.ToBase64String(bytes);
    }

    public static string UnprotectKey(string value)
    {
        if (!value.StartsWith(DpapiPrefix, StringComparison.Ordinal)) return value;
        if (!OperatingSystem.IsWindows()) throw new InvalidOperationException("DPAPI 로 감싼 키는 Windows 에서만 풀 수 있습니다.");
        try
        {
            var bytes = ProtectedData.Unprotect(Convert.FromBase64String(value[DpapiPrefix.Length..]), null, DataProtectionScope.LocalMachine);
            return Encoding.UTF8.GetString(bytes);
        }
        catch (CryptographicException ex)
        {
            // 다른 PC 에서 옮겨 온 설정 파일이다 — 조용히 틀린 키로 뜨지 않고 이유를 말한다
            throw new InvalidOperationException(
                "server.json 의 Jwt 키를 풀지 못했습니다. 다른 PC 에서 옮겨 온 파일이면 이 PC 에서 configure --jwt-key 로 다시 넣으세요.", ex);
        }
    }

    /// <summary>
    /// SYSTEM·Administrators, 그리고 configure 를 실행한 계정만 읽고 쓰게 한다.
    /// 상속을 끊어 부모 폴더의 Users 읽기 권한이 내려오지 않게 한다.
    /// 설치 패키지에서는 실행 계정이 곧 SYSTEM 이다 (deferred · Impersonate=no).
    /// </summary>
    private static void RestrictToAdministrators(string path)
    {
        if (!OperatingSystem.IsWindows()) return;
        var security = new FileSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);

        var sids = new List<SecurityIdentifier>
        {
            new(WellKnownSidType.LocalSystemSid, null),
            new(WellKnownSidType.BuiltinAdministratorsSid, null),
        };
        if (WindowsIdentity.GetCurrent().User is { } me && !sids.Contains(me)) sids.Add(me);

        foreach (var sid in sids)
            security.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl, AccessControlType.Allow));
        new FileInfo(path).SetAccessControl(security);
    }

    private static string? ReadString(JsonObject root, string section, string key) =>
        root[section] is JsonObject s && s[key] is JsonValue v && v.TryGetValue<string>(out var text) ? text : null;

    private static int? ReadInt(JsonObject root, string section, string key) =>
        root[section] is JsonObject s && s[key] is JsonValue v && v.TryGetValue<int>(out var n) ? n : null;
}
