using System.Text.Json;
using BODA.VMS.MLOps.TrainWorker;
using BODA.VMS.MLOps.TrainWorker.Agent;
using BODA.VMS.MLOps.TrainWorker.Environment;
using BODA.VMS.MLOps.TrainWorker.Jobs;
using Microsoft.Extensions.Options;

// ── CLI ──
//   configure : BODA.VMS.MLOps.TrainWorker configure --server http://server:5310 --token wk_… [--name GPU-01] [--python C:\path\python.exe]
//                                                    [--wheels <오프라인 wheel 폴더>] [--cache <캐시 루트>] [--gpu 0] [--cpu]
//               → %ProgramData%\BODA VMS TrainWorker\worker.json (토큰은 DPAPI LocalMachine 으로 보호). 설치 패키지(MSI)도 이 명령을 부른다.
//   diag      : BODA.VMS.MLOps.TrainWorker diag
//               → 설정된 파이썬(없으면 venv 부트스트랩)으로 자기진단을 돌려 결과를 찍는다. 설치 뒤 GPU·패키지를 눈으로 확인하는 용도.
//   (없음)    : 서비스/콘솔 실행
if (args.Length > 0 && args[0].Equals("configure", StringComparison.OrdinalIgnoreCase))
    return Configure(args.Skip(1).ToArray());
if (args.Length > 0 && args[0].Equals("diag", StringComparison.OrdinalIgnoreCase))
    return await DiagAsync();

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddWindowsService(o => o.ServiceName = "BodaVmsTrainWorker");

// 설치 시 생성된 worker.json 을 appsettings 위에 덮는다
if (File.Exists(WorkerOptions.ConfigFilePath))
    builder.Configuration.AddJsonFile(WorkerOptions.ConfigFilePath, optional: true, reloadOnChange: false);

builder.Services.Configure<WorkerOptions>(builder.Configuration.GetSection(WorkerOptions.Section));

// 파일 로그 (Phase 3 §10): 서비스로 돌 때 콘솔은 아무 데도 가지 않으므로 logs\worker-yyyyMMdd.log 에 남긴다
{
    var early = new WorkerOptions();
    builder.Configuration.GetSection(WorkerOptions.Section).Bind(early);
    builder.Logging.AddProvider(new FileLoggerProvider(early.LogsDir));
}

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddHttpClient<ServerClient>();
builder.Services.AddSingleton<PythonEnvironment>();
builder.Services.AddSingleton<TrainingProcessHost>();
builder.Services.AddSingleton<DatasetCache>();
builder.Services.AddSingleton<PretrainedCache>();
builder.Services.AddSingleton<ScriptStore>();
builder.Services.AddSingleton<JobRunner>();
builder.Services.AddHostedService<WorkerAgent>();

var host = builder.Build();
host.Run();
return 0;

static int Configure(string[] a)
{
    string? server = null, token = null, name = null, python = null, wheels = null, cache = null;
    int? gpu = null;
    bool cpu = false;
    try
    {
        for (int i = 0; i < a.Length; i++)
        {
            switch (a[i])
            {
                case "--server": server = Next(a, ref i); break;
                case "--token": token = Next(a, ref i); break;
                case "--name": name = Next(a, ref i); break;
                case "--python": python = Next(a, ref i); break;
                case "--wheels": wheels = Next(a, ref i); break;
                case "--cache": cache = Next(a, ref i); break;
                case "--gpu":
                    if (!int.TryParse(Next(a, ref i), out var g) || g < 0) { Console.Error.WriteLine("--gpu 는 0 이상의 정수"); return 2; }
                    gpu = g; break;
                case "--cpu": cpu = true; break;
                default: Console.Error.WriteLine($"알 수 없는 옵션: {a[i]}"); return 2;
            }
        }
    }
    catch (ArgumentException ex) { Console.Error.WriteLine(ex.Message); return 2; }

    if (string.IsNullOrWhiteSpace(server) || string.IsNullOrWhiteSpace(token))
    {
        Console.Error.WriteLine("사용법: configure --server <url> --token <wk_…> [--name <이름>] [--python <python.exe>] [--wheels <폴더>] [--cache <폴더>] [--gpu <n>] [--cpu]");
        return 2;
    }
    if (!Uri.TryCreate(server.Trim(), UriKind.Absolute, out var uri) || (uri.Scheme != "http" && uri.Scheme != "https"))
    {
        Console.Error.WriteLine($"--server 는 http(s):// 주소여야 합니다: {server}");
        return 2;
    }
    if (!string.IsNullOrWhiteSpace(wheels) && !Directory.Exists(wheels))
        Console.Error.WriteLine($"경고: --wheels 폴더가 없습니다 (첫 시작 때 pip 가 실패합니다): {wheels}");

    Directory.CreateDirectory(WorkerOptions.DefaultRoot);
    var worker = new Dictionary<string, object?>
    {
        ["ServerUrl"] = uri.GetLeftPart(UriPartial.Authority) + uri.AbsolutePath.TrimEnd('/'),
        ["Token"] = WorkerOptions.ProtectToken(token.Trim()),
        ["Name"] = string.IsNullOrWhiteSpace(name) ? System.Environment.MachineName : name.Trim(),
        ["AllowCpuOnly"] = cpu,
    };
    if (!string.IsNullOrWhiteSpace(python)) worker["PythonExe"] = python.Trim();
    if (!string.IsNullOrWhiteSpace(wheels)) worker["WheelBundleDir"] = Path.GetFullPath(wheels.Trim());
    if (!string.IsNullOrWhiteSpace(cache)) worker["CacheRoot"] = Path.GetFullPath(cache.Trim());
    if (gpu is not null) worker["GpuIndex"] = gpu.Value;

    var json = JsonSerializer.Serialize(new { Worker = worker }, new JsonSerializerOptions { WriteIndented = true });
    File.WriteAllText(WorkerOptions.ConfigFilePath, json);
    Console.WriteLine($"설정 저장: {WorkerOptions.ConfigFilePath}");
    return 0;

    static string Next(string[] a, ref int i)
    {
        if (i + 1 >= a.Length) throw new ArgumentException($"{a[i]} 뒤에 값이 없습니다");
        return a[++i];
    }
}

static async Task<int> DiagAsync()
{
    // 서비스 호스트와 같은 순서: appsettings → worker.json → 환경변수(Worker__*). 시험할 때 환경변수로 덮어쓸 수 있게 한다.
    var cfg = new ConfigurationBuilder()
        .AddJsonFile(Path.Combine(AppContext.BaseDirectory, "appsettings.json"), optional: true)
        .AddJsonFile(WorkerOptions.ConfigFilePath, optional: true)
        .AddEnvironmentVariables()
        .Build();
    var o = new WorkerOptions();
    cfg.GetSection(WorkerOptions.Section).Bind(o);
    o.EnsureDirectories();

    using var lf = LoggerFactory.Create(b => b.AddSimpleConsole(c => { c.SingleLine = true; c.TimestampFormat = "HH:mm:ss "; }).SetMinimumLevel(LogLevel.Information));
    var env = new PythonEnvironment(Options.Create(o), lf.CreateLogger<PythonEnvironment>());
    try
    {
        var py = await env.EnsureAsync(CancellationToken.None);
        var caps = await env.DiagnoseAsync(py, PythonEnvironment.ScriptsHash(o.ScriptsDir), CancellationToken.None);
        Console.WriteLine();
        Console.WriteLine($"python      : {py}");
        Console.WriteLine($"cuda        : {(caps.CudaAvailable ? "사용 가능" : "없음")}  {caps.GpuName} {(caps.GpuMemMB > 0 ? caps.GpuMemMB + "MB" : "")} {caps.CudaVersion}".TrimEnd());
        Console.WriteLine("packages    : " + string.Join(", ", caps.Packages.OrderBy(k => k.Key).Select(k => $"{k.Key} {k.Value}")));
        Console.WriteLine($"disk free   : {caps.DiskFreeGB:F1} GB  (캐시 {o.ResolvedCacheRoot()})");
        Console.WriteLine($"server      : {o.ServerUrl}  (토큰 {(string.IsNullOrWhiteSpace(o.ResolveToken()) ? "없음" : "있음")})");
        if (caps.DiagnosticsOk) { Console.WriteLine("진단 결과   : 정상 — 서버에 등록되면 작업을 받습니다"); return 0; }
        Console.WriteLine("진단 결과   : 실패 — 서버가 이 워커를 Disabled 로 표시합니다");
        foreach (var f in caps.DiagnosticFailures!) Console.WriteLine("  - " + f);
        return 1;
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine("진단 실패: " + ex.Message);
        return 1;
    }
}
