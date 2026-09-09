using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using BODA.VMS.MLOps.Contracts;
using BODA.VMS.MLOps.Contracts.Training;
using BODA.VMS.MLOps.Contracts.Workers;
using BODA.VMS.MLOps.Core.Hashing;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BODA.VMS.MLOps.TrainWorker.Agent;

/// <summary>서버 응답 오류 (ApiError 포함). 4xx 검증 오류는 재시도하지 않는다.</summary>
public sealed class ServerApiException(HttpStatusCode status, ApiError? error, string url)
    : Exception($"{(int)status} {status} {url}: {error?.Code} {error?.Message}")
{
    public HttpStatusCode Status { get; } = status;
    public ApiError? Error { get; } = error;
    public bool IsClientError => (int)Status is >= 400 and < 500;
}

/// <summary>워커 → 서버 HTTP 클라이언트 (Phase 3 §4). 서버로만 아웃바운드, Bearer 워커 토큰, X-Worker-Protocol 헤더.</summary>
public sealed class ServerClient
{
    private readonly HttpClient _http;
    private readonly ILogger<ServerClient> _logger;
    private static readonly JsonSerializerOptions Json = MlopsJson.Options;

    public ServerClient(HttpClient http, IOptions<WorkerOptions> options, ILogger<ServerClient> logger)
    {
        _http = http;
        _logger = logger;
        var o = options.Value;
        _http.BaseAddress = new Uri(o.ServerUrl.TrimEnd('/') + "/");
        _http.Timeout = System.Threading.Timeout.InfiniteTimeSpan; // 요청별 CancellationToken 으로 제어
        var token = o.ResolveToken();
        if (!string.IsNullOrEmpty(token))
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        _http.DefaultRequestHeaders.Add(MlopsJson.ProtocolHeader, MlopsJson.ProtocolVersion.ToString());
        _http.DefaultRequestHeaders.UserAgent.ParseAdd($"BODA-VMS-TrainWorker/{WorkerVersion}");
    }

    public static string WorkerVersion => typeof(ServerClient).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

    public Task<RegisterWorkerResponse> RegisterAsync(RegisterWorkerRequest req, CancellationToken ct) =>
        PostAsync<RegisterWorkerRequest, RegisterWorkerResponse>("api/workers/register", req, ct);

    public Task<HeartbeatResponse> HeartbeatAsync(Guid workerId, HeartbeatRequest req, CancellationToken ct) =>
        PostAsync<HeartbeatRequest, HeartbeatResponse>($"api/workers/{workerId}/heartbeat", req, ct);

    /// <summary>long-poll. 작업이 없으면 null(204).</summary>
    public async Task<JobAssignment?> NextJobAsync(int waitSec, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(waitSec + 30));
        using var res = await _http.GetAsync($"api/training-jobs/next?wait={waitSec}", timeout.Token);
        if (res.StatusCode == HttpStatusCode.NoContent) return null;
        await EnsureSuccessAsync(res, "api/training-jobs/next");
        return await res.Content.ReadFromJsonAsync<JobAssignment>(Json, timeout.Token);
    }

    public Task<TrainingJobDto> AckAsync(Guid jobId, CancellationToken ct) =>
        PostAsync<object?, TrainingJobDto>($"api/training-jobs/{jobId}/ack", null, ct);

    private sealed record ProgressResponse(TrainingJobDto Job, bool CancelRequested);

    /// <summary>진행률 보고. 반환: 서버가 취소를 요청했는지.</summary>
    public async Task<bool> ProgressAsync(Guid jobId, ProgressReport report, CancellationToken ct)
    {
        using var timeout = WithTimeout(ct, 30);
        using var res = await _http.PatchAsJsonAsync($"api/training-jobs/{jobId}/progress", report, Json, timeout.Token);
        await EnsureSuccessAsync(res, "progress");
        var body = await res.Content.ReadFromJsonAsync<ProgressResponse>(Json, timeout.Token);
        return body?.CancelRequested ?? false;
    }

    public async Task<ArtifactsResponse> UploadArtifactsAsync(Guid jobId, IReadOnlyList<(string Field, string Path)> files, CancellationToken ct)
    {
        using var content = new MultipartFormDataContent();
        var streams = new List<Stream>();
        try
        {
            foreach (var (field, path) in files)
            {
                // train.log 는 아직 워커가 열어 둘 수 있으므로 쓰기 공유를 허용해 읽는다
                var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16, useAsync: true);
                streams.Add(fs);
                var part = new StreamContent(fs);
                part.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
                content.Add(part, field, Path.GetFileName(path));
            }
            using var timeout = WithTimeout(ct, 3600);
            using var res = await _http.PostAsync($"api/training-jobs/{jobId}/artifacts", content, timeout.Token);
            await EnsureSuccessAsync(res, "artifacts");
            return (await res.Content.ReadFromJsonAsync<ArtifactsResponse>(Json, timeout.Token))!;
        }
        finally
        {
            foreach (var s in streams) await s.DisposeAsync();
        }
    }

    public Task<TrainingJobDto> FinishAsync(Guid jobId, FinishJobRequest req, CancellationToken ct) =>
        PostAsync<FinishJobRequest, TrainingJobDto>($"api/training-jobs/{jobId}/finish", req, ct);

    public async Task<ScriptsManifest> ScriptsManifestAsync(CancellationToken ct)
    {
        using var timeout = WithTimeout(ct, 30);
        return (await _http.GetFromJsonAsync<ScriptsManifest>("api/workers/scripts", Json, timeout.Token))!;
    }

    /// <summary>
    /// 파일 다운로드 — 임시 파일 → SHA-256 검증(expectedSha 또는 서버 X-Content-Sha256) → 원자적 이동.
    /// 반환: 실제 SHA-256.
    ///
    /// <para>
    /// 헤더로 대조하는 것은 <b>파일 바이트</b>의 해시다. 서버가 데이터셋 내보내기에 그 값을 싣는다
    /// (<c>DatasetVersion.ExportSha256</c>). 내용의 신원인 <c>ManifestHash</c> 와 헷갈리지 마세요 —
    /// 한동안 서버가 그쪽을 실어서, 스냅샷으로 만든 판은 여기서 늘 "해시 불일치" 로 막혔습니다.
    /// </para>
    /// </summary>
    public async Task<string> DownloadAsync(string relativeUrl, string destPath, string? expectedSha, CancellationToken ct)
    {
        var url = relativeUrl.TrimStart('/');
        var temp = destPath + ".part";
        Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
        try
        {
            using var res = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
            await EnsureSuccessAsync(res, url);
            await using (var body = await res.Content.ReadAsStreamAsync(ct))
            await using (var fs = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true))
                await body.CopyToAsync(fs, ct);

            var sha = await Sha256Util.HashFileAsync(temp, ct);
            var expected = expectedSha;
            if (expected is null && res.Headers.TryGetValues("X-Content-Sha256", out var hv)) expected = hv.FirstOrDefault();
            if (expected is not null && !sha.Equals(expected, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"다운로드 해시 불일치 {url}: 기대 {expected[..12]}… 실제 {sha[..12]}…");

            File.Move(temp, destPath, overwrite: true);
            return sha;
        }
        finally
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch { }
        }
    }

    private async Task<TRes> PostAsync<TReq, TRes>(string url, TReq? body, CancellationToken ct)
    {
        using var timeout = WithTimeout(ct, 60);
        using var res = body is null
            ? await _http.PostAsync(url, new StringContent("{}", System.Text.Encoding.UTF8, "application/json"), timeout.Token)
            : await _http.PostAsJsonAsync(url, body, Json, timeout.Token);
        await EnsureSuccessAsync(res, url);
        return (await res.Content.ReadFromJsonAsync<TRes>(Json, timeout.Token))!;
    }

    private static CancellationTokenSource WithTimeout(CancellationToken ct, int seconds)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(seconds));
        return cts;
    }

    private async Task EnsureSuccessAsync(HttpResponseMessage res, string url)
    {
        if (res.IsSuccessStatusCode) return;
        ApiError? error = null;
        try
        {
            var text = await res.Content.ReadAsStringAsync();
            if (!string.IsNullOrWhiteSpace(text)) error = JsonSerializer.Deserialize<ApiError>(text, Json);
        }
        catch { /* 본문이 JSON 이 아님 */ }
        _logger.LogWarning("서버 오류 {Status} {Url}: {Code} {Message}", (int)res.StatusCode, url, error?.Code, error?.Message);
        throw new ServerApiException(res.StatusCode, error, url);
    }
}
