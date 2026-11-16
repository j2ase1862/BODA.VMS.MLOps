namespace BODA.VMS.MLOps.TrainWorker;

/// <summary>
/// 워커 설정을 어디서 어떤 순서로 읽는가 (ML-32).
///
/// <para><b>우선순위: 명령줄 &gt; 환경변수 &gt; 설정 파일(worker.json) &gt; appsettings.</b>
/// 예전에는 worker.json 을 호스트 빌더가 환경변수·명령줄을 읽은 <b>뒤에</b> 덧붙여 파일이 가장 셌다.
/// 그래서 한 PC 에 두 번째 워커를 띄우려 <c>--Worker:Name</c> 이나 <c>Worker__CacheRoot</c> 를 줘도
/// 첫 워커의 worker.json 이 이겨, 같은 이름·같은 캐시로 붙었다.</para>
///
/// <para><b>설정 파일 고르기:</b> <c>--config &lt;경로&gt;</c> &gt; 환경변수 <c>BODA_WORKER_CONFIG</c> &gt;
/// 기본 <c>%ProgramData%\BODA VMS TrainWorker\worker.json</c>. 아무것도 주지 않으면 지금까지와 똑같이
/// 기본 파일을 읽는다 — MSI 가 설치한 서비스(인자 없음)는 그대로 돈다.</para>
/// </summary>
public static class WorkerConfiguration
{
    public const string ConfigArg = "--config";
    public const string ConfigEnvVar = "BODA_WORKER_CONFIG";

    /// <summary>쓸 설정 파일 경로. <paramref name="environment"/> 를 주지 않으면 프로세스 환경변수를 본다.</summary>
    public static string ResolveConfigPath(IReadOnlyList<string> args, Func<string, string?>? environment = null)
    {
        for (int i = 0; i < args.Count; i++)
        {
            if (args[i].Equals(ConfigArg, StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 >= args.Count || string.IsNullOrWhiteSpace(args[i + 1]))
                    throw new ArgumentException($"{ConfigArg} 뒤에 설정 파일 경로가 없습니다");
                return Path.GetFullPath(args[i + 1].Trim());
            }
            if (args[i].StartsWith(ConfigArg + "=", StringComparison.OrdinalIgnoreCase))
                return Path.GetFullPath(args[i][(ConfigArg.Length + 1)..].Trim());
        }
        var fromEnv = (environment ?? System.Environment.GetEnvironmentVariable)(ConfigEnvVar);
        return string.IsNullOrWhiteSpace(fromEnv) ? WorkerOptions.ConfigFilePath : Path.GetFullPath(fromEnv.Trim());
    }

    /// <summary><c>--config 경로</c> 를 뺀 나머지 인자 — 명령줄 설정 공급자와 하위 명령이 그 둘을 모르게.</summary>
    public static string[] WithoutConfigArg(IReadOnlyList<string> args)
    {
        var rest = new List<string>();
        for (int i = 0; i < args.Count; i++)
        {
            if (args[i].Equals(ConfigArg, StringComparison.OrdinalIgnoreCase)) { i++; continue; }
            if (args[i].StartsWith(ConfigArg + "=", StringComparison.OrdinalIgnoreCase)) continue;
            rest.Add(args[i]);
        }
        return rest.ToArray();
    }

    /// <summary>
    /// 설정 파일 위에 환경변수, 그 위에 명령줄을 얹는다. 호스트 빌더가 이미 읽은 환경변수·명령줄이 있어도
    /// 마지막에 다시 얹으므로 순서가 이것으로 정해진다(뒤에 얹은 것이 이긴다).
    /// <paramref name="environment"/> 는 시험용 — 주지 않으면 프로세스 환경변수(<c>Worker__Name</c> 형태)를 쓴다.
    /// </summary>
    public static IConfigurationBuilder Layer(IConfigurationBuilder builder, string configPath, string[] args,
        IReadOnlyDictionary<string, string?>? environment = null)
    {
        builder.AddJsonFile(configPath, optional: true, reloadOnChange: false);
        if (environment is null) builder.AddEnvironmentVariables();
        else builder.AddInMemoryCollection(environment.ToDictionary(kv => kv.Key.Replace("__", ":"), kv => kv.Value));
        builder.AddCommandLine(WithoutConfigArg(args));
        return builder;
    }
}
