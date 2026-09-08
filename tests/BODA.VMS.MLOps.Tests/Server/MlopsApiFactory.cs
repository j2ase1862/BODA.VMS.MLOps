using System.IO.Compression;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using BODA.VMS.MLOps.Contracts;
using BODA.VMS.MLOps.Contracts.Datasets;
using BODA.VMS.MLOps.Contracts.Models;
using BODA.VMS.MLOps.Contracts.Workers;
using BODA.VMS.MLOps.Core.Domain;
using BODA.VMS.MLOps.Server.Auth;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace BODA.VMS.MLOps.Tests.Server;

/// <summary>테스트용 수동 시계 — 하트비트 소실·ack 타임아웃을 시간 전진으로 재현</summary>
public sealed class ManualTimeProvider : TimeProvider
{
    private DateTimeOffset _now = DateTimeOffset.UtcNow;
    public override DateTimeOffset GetUtcNow() => _now;
    public void Advance(TimeSpan by) => _now += by;
}

/// <summary>
/// 서버 통합 테스트 팩토리 — 임시 SQLite·스토리지, 개발 토큰 발급 허용, 스크립트 폴더에 train_fake.py 를 train_dfine.py 등으로 배치.
/// 감독자(JobSupervisor)는 주기를 길게 두고 테스트에서 직접 호출한다.
/// </summary>
public class MlopsApiFactory : WebApplicationFactory<ServerEntryPoint>
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "mlops-tests", Guid.NewGuid().ToString("N"));
    public string ScriptsRoot => Path.Combine(Root, "scripts");
    public string StorageRoot => Path.Combine(Root, "storage");
    public ManualTimeProvider Clock { get; } = new();
    public static readonly JsonSerializerOptions Json = MlopsJson.Options;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        Directory.CreateDirectory(ScriptsRoot);
        var fake = Path.Combine(AppContext.BaseDirectory, "scripts", "train_fake.py");
        foreach (var name in new[] { "train_dfine.py", "train_classifier.py", "train_anomaly.py", "train_yolo.py" })
            File.Copy(fake, Path.Combine(ScriptsRoot, name), overwrite: true);

        builder.UseEnvironment("Testing");
        builder.UseSetting("ConnectionStrings:DefaultConnection", $"Data Source={Path.Combine(Root, "test.db")}");
        builder.UseSetting("Jwt:Key", "test-key-0123456789abcdef0123456789abcdef");
        builder.UseSetting("Jwt:Issuer", "BODA.VMS.Web");
        builder.UseSetting("Jwt:Audience", "BODA.VMS.Web");
        builder.UseSetting("Auth:EnableDevTokens", "true");
        builder.UseSetting("Mlops:StorageRoot", StorageRoot);
        builder.UseSetting("Mlops:ScriptsRoot", ScriptsRoot);
        builder.UseSetting("Mlops:SupervisorIntervalSec", "3600");
        builder.UseSetting("Mlops:MaxModelBytes", (64L * 1024 * 1024).ToString());
        // SAM 은 기본으로 꺼 둔다. 서버 프로젝트 폴더에 모델을 둔 개발 PC 에서만 켜지면
        // 같은 시험이 사람마다 다르게 도는 셈이 된다. 켜는 쪽은 SamEnabledFactory 가 따로 맡는다.
        builder.UseSetting("Sam:EncoderPath", "");
        builder.UseSetting("Sam:DecoderPath", "");
        builder.UseSetting("Urls", "");
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(Clock);
        });
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        try { if (Directory.Exists(Root)) Directory.Delete(Root, true); } catch { }
    }

    // ───────────── 인증 헬퍼 ─────────────

    public async Task<HttpClient> ClientAsAsync(string user, params string[] roles)
    {
        var anon = CreateClient();
        var res = await anon.PostAsJsonAsync("/api/auth/dev-token", new { user, roles });
        res.EnsureSuccessStatusCode();
        var token = (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString()!;
        var c = CreateClient();
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return c;
    }

    public Task<HttpClient> AdminAsync() => ClientAsAsync("admin", Roles.Admin);
    public Task<HttpClient> EngineerAsync() => ClientAsAsync("engineer", Roles.Engineer);
    public Task<HttpClient> ViewerAsync() => ClientAsAsync("viewer", Roles.Viewer);
    public Task<HttpClient> LineAsync() => ClientAsAsync("line-01", Roles.Line);

    public async Task<CreateWorkerResponse> CreateWorkerAsync(HttpClient admin, string name = "gpu-01", TaskType[]? taskTypes = null)
    {
        var res = await admin.PostAsJsonAsync("/api/workers", new CreateWorkerRequest(name, taskTypes), Json);
        res.EnsureSuccessStatusCode();
        return (await res.Content.ReadFromJsonAsync<CreateWorkerResponse>(Json))!;
    }

    public HttpClient WorkerClient(string token)
    {
        var c = CreateClient();
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        c.DefaultRequestHeaders.Add(MlopsJson.ProtocolHeader, MlopsJson.ProtocolVersion.ToString());
        return c;
    }

    public static WorkerCapabilities OkCapabilities(double diskFreeGB = 500) =>
        new("RTX 4090", 24576, "12.4", true, "3.12.4", new Dictionary<string, string> { ["torch"] = "2.6.0" }, "abc", diskFreeGB, "0.1.0", ["CUDAExecutionProvider"]);

    // ───────────── 데이터 헬퍼 ─────────────

    public static async Task<ModelDto> CreateModelAsync(HttpClient engineer, string name = "scratch", TaskType taskType = TaskType.Detection, string[]? classes = null)
    {
        var res = await engineer.PostAsJsonAsync("/api/models", new CreateModelRequest(name, taskType, classes ?? ["good", "defect"]), Json);
        res.EnsureSuccessStatusCode();
        return (await res.Content.ReadFromJsonAsync<ModelDto>(Json))!;
    }

    public static async Task<HttpResponseMessage> UploadVersionAsync(HttpClient client, Guid modelId, byte[] onnx, VersionUploadMeta? meta = null)
    {
        using var form = new MultipartFormDataContent();
        form.Add(new ByteArrayContent(onnx) { Headers = { ContentType = new MediaTypeHeaderValue("application/octet-stream") } }, "file", "best.onnx");
        if (meta is not null) form.Add(new StringContent(JsonSerializer.Serialize(meta, Json), Encoding.UTF8, "application/json"), "meta");
        return await client.PostAsync($"/api/models/{modelId}/versions", form);
    }

    /// <summary>YOLO 형식 최소 데이터셋 zip (data.yaml + images/labels 폴더)</summary>
    public static byte[] MakeYoloDatasetZip(int seedByte = 1)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            Write(zip, "data.yaml", "path: .\ntrain: images/train\nval: images/val\nnames:\n  0: good\n  1: defect\n");
            Write(zip, "images/train/a.jpg", new string((char)('a' + seedByte), 64));
            Write(zip, "images/val/b.jpg", "bbbb");
            Write(zip, "labels/train/a.txt", "0 0.5 0.5 0.2 0.2\n");
            Write(zip, "labels/val/b.txt", "1 0.5 0.5 0.2 0.2\n");
        }
        return ms.ToArray();

        static void Write(ZipArchive zip, string name, string content)
        {
            var e = zip.CreateEntry(name);
            using var w = new StreamWriter(e.Open());
            w.Write(content);
        }
    }

    public static async Task<DatasetVersionDto> UploadDatasetAsync(HttpClient engineer, string name = "ds-1", TaskType taskType = TaskType.Detection, string format = "yolo", byte[]? zip = null)
    {
        using var form = new MultipartFormDataContent();
        form.Add(new ByteArrayContent(zip ?? MakeYoloDatasetZip()) { Headers = { ContentType = new MediaTypeHeaderValue("application/zip") } }, "file", "ds.zip");
        form.Add(new StringContent(JsonSerializer.Serialize(new DatasetVersionUploadMeta(name, taskType, format, 2, ["good", "defect"]), Json), Encoding.UTF8, "application/json"), "meta");
        var res = await engineer.PostAsync("/api/dataset-versions", form);
        res.EnsureSuccessStatusCode();
        return (await res.Content.ReadFromJsonAsync<DatasetVersionDto>(Json))!;
    }

    public static async Task<ApiError?> ErrorAsync(HttpResponseMessage res) =>
        await res.Content.ReadFromJsonAsync<ApiError>(Json);
}
