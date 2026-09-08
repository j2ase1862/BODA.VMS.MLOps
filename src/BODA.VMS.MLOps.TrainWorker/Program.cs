using System.Text.Json;
using BODA.VMS.MLOps.TrainWorker;
using BODA.VMS.MLOps.TrainWorker.Agent;
using BODA.VMS.MLOps.TrainWorker.Environment;
using BODA.VMS.MLOps.TrainWorker.Jobs;

// ── CLI: configure ──
//   BODA.VMS.MLOps.TrainWorker configure --server http://server:5310 --token wk_… [--name GPU-01] [--python C:\path\python.exe] [--cpu]
//   → %ProgramData%\BODA VMS TrainWorker\worker.json (토큰은 DPAPI 로 보호)
if (args.Length > 0 && args[0].Equals("configure", StringComparison.OrdinalIgnoreCase))
    return Configure(args.Skip(1).ToArray());

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddWindowsService(o => o.ServiceName = "BodaVmsTrainWorker");

// 설치 시 생성된 worker.json 을 appsettings 위에 덮는다
if (File.Exists(WorkerOptions.ConfigFilePath))
    builder.Configuration.AddJsonFile(WorkerOptions.ConfigFilePath, optional: true, reloadOnChange: false);

builder.Services.Configure<WorkerOptions>(builder.Configuration.GetSection(WorkerOptions.Section));
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
    string? server = null, token = null, name = null, python = null;
    bool cpu = false;
    for (int i = 0; i < a.Length; i++)
    {
        switch (a[i])
        {
            case "--server": server = a[++i]; break;
            case "--token": token = a[++i]; break;
            case "--name": name = a[++i]; break;
            case "--python": python = a[++i]; break;
            case "--cpu": cpu = true; break;
            default: Console.Error.WriteLine($"알 수 없는 옵션: {a[i]}"); return 2;
        }
    }
    if (string.IsNullOrWhiteSpace(server) || string.IsNullOrWhiteSpace(token))
    {
        Console.Error.WriteLine("사용법: configure --server <url> --token <wk_…> [--name <이름>] [--python <python.exe>] [--cpu]");
        return 2;
    }
    Directory.CreateDirectory(WorkerOptions.DefaultRoot);
    var worker = new Dictionary<string, object?>
    {
        ["ServerUrl"] = server.TrimEnd('/'),
        ["Token"] = WorkerOptions.ProtectToken(token.Trim()),
        ["Name"] = name ?? Environment.MachineName,
        ["AllowCpuOnly"] = cpu,
    };
    if (!string.IsNullOrWhiteSpace(python)) worker["PythonExe"] = python;
    var json = JsonSerializer.Serialize(new { Worker = worker }, new JsonSerializerOptions { WriteIndented = true });
    File.WriteAllText(WorkerOptions.ConfigFilePath, json);
    Console.WriteLine($"설정 저장: {WorkerOptions.ConfigFilePath}");
    return 0;
}
