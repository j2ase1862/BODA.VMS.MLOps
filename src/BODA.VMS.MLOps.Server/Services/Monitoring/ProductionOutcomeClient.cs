using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using BODA.VMS.MLOps.Server.Auth;
using Microsoft.Extensions.Options;

namespace BODA.VMS.MLOps.Server.Services.Monitoring;

/// <summary>운영 웹이 돌려주는 모델별 집계 한 줄 (BODA.VMS.Web 의 ModelOutcomeDto 와 같은 모양).</summary>
public sealed class ProductionOutcome
{
    [JsonPropertyName("modelVersion")] public string? ModelVersion { get; set; }
    [JsonPropertyName("date")] public DateTime? Date { get; set; }
    [JsonPropertyName("totalCount")] public int TotalCount { get; set; }
    [JsonPropertyName("passCount")] public int PassCount { get; set; }
    [JsonPropertyName("ngCount")] public int NgCount { get; set; }
    [JsonPropertyName("avgConfidence")] public double? AvgConfidence { get; set; }
    [JsonPropertyName("minConfidence")] public double? MinConfidence { get; set; }
    [JsonPropertyName("confidenceSampleCount")] public int ConfidenceSampleCount { get; set; }
    [JsonPropertyName("avgBrightness")] public double? AvgBrightness { get; set; }
    [JsonPropertyName("avgFocusScore")] public double? AvgFocusScore { get; set; }
    [JsonPropertyName("avgCycleTimeMs")] public double? AvgCycleTimeMs { get; set; }
}

/// <summary>운영 웹에 닿지 못했거나 답을 읽지 못했을 때.</summary>
public sealed class ProductionOutcomeException(string message, Exception? inner = null)
    : Exception(message, inner);

/// <summary>
/// 운영 웹(BODA.VMS.Web)에서 모델별 검사 결과를 당겨 온다 (Phase 5 — 모니터링).
///
/// <para><b>왜 당겨 오는가.</b>
/// 두 시스템이 DB 를 공유하지 않는다. 운영 쪽에 MLOps 를 위한 쓰기 경로를 만들면
/// 검사 라인의 안정성에 MLOps 가 얹히게 된다 — 그쪽이 멈추면 안 되는 시스템이다.
/// 그래서 읽기 전용 API 를 이쪽에서 당겨 온다. 못 당겨 와도 MLOps 의 다른 기능은 그대로 돈다.
/// </para>
/// <para><b>인증.</b>
/// 운영 웹의 기계용 API 키(<c>X-API-Key</c>)를 쓴다 — 라인 PC 가 붙는 것과 같은 방식이다.
/// 예전에는 두 서버가 같은 서명 키를 쓴다는 점을 이용해 MLOps 가 스스로 JWT 를 만들어 붙었는데,
/// 운영 웹이 토큰 세대 검사를 넣으면서 <b>사람 계정이 아닌 토큰</b>이 401 로 막혔다 (2026-09-16).
/// 읽기 전용 집계라 키 하나로 충분하고, 이 키가 새더라도 운영 데이터를 고칠 수 없다.
/// </para>
/// <para>구버전 운영 웹(기계용 전환 이전)은 이 경로에 JWT 를 요구한다. 그런 서버에도 붙도록
/// 토큰을 함께 보낸다 — 새 서버는 무시하고, 구버전 서버는 그것으로 통과한다.</para>
/// </summary>
public sealed class ProductionOutcomeClient(
    HttpClient http,
    ServiceTokenIssuer tokens,
    IOptions<MonitoringOptions> options,
    ILogger<ProductionOutcomeClient> logger)
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>운영 웹에 붙을 때 쓰는 이름. 감사 로그에 이 이름이 남는다.</summary>
    public const string ServiceUser = "mlops-monitor";

    /// <summary>운영 웹의 기계용 API 키 헤더 — 라인 PC 가 쓰는 것과 같다.</summary>
    public const string ApiKeyHeader = "X-API-Key";

    /// <summary>
    /// 기간 안의 모델별 결과를 받는다. <paramref name="daily"/> 면 모델×날짜로 나눠 온다.
    /// </summary>
    public async Task<IReadOnlyList<ProductionOutcome>> GetAsync(
        string baseUrl, DateTime startUtc, DateTime endUtc, int? clientId, bool daily, CancellationToken ct)
    {
        var url = $"{baseUrl.TrimEnd('/')}/api/history/model-outcomes" +
                  $"?startDate={Uri.EscapeDataString(startUtc.ToString("O"))}" +
                  $"&endDate={Uri.EscapeDataString(endUtc.ToString("O"))}" +
                  $"&daily={(daily ? "true" : "false")}";
        if (clientId is { } id) url += $"&clientId={id}";

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (!string.IsNullOrWhiteSpace(options.Value.ApiKey))
            request.Headers.TryAddWithoutValidation(ApiKeyHeader, options.Value.ApiKey);
        // 구버전 운영 웹 호환 — 그쪽은 이 경로에 JWT 를 요구한다. 새 서버는 이 헤더를 보지 않는다.
        // (운영 웹이 검증하는 audience 로 발급한다. 우리 것으로 만들면 서명 키가 같아도 401 이다.)
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer", tokens.Issue(ServiceUser, [Roles.Viewer], audience: options.Value.Audience));

        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(request, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            throw new ProductionOutcomeException($"운영 웹에 닿지 못했습니다: {baseUrl}", ex);
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                var body = await SafeReadAsync(response, ct);
                throw new ProductionOutcomeException(
                    $"운영 웹이 {(int)response.StatusCode} 로 답했습니다: {body}");
            }

            var json = await response.Content.ReadAsStringAsync(ct);
            var outcomes = JsonSerializer.Deserialize<List<ProductionOutcome>>(json, Json);
            if (outcomes is null)
                throw new ProductionOutcomeException("운영 웹의 답을 읽지 못했습니다.");

            logger.LogDebug("운영 웹에서 모델별 결과 {Count}줄을 받았습니다 ({Start:d}~{End:d})",
                outcomes.Count, startUtc, endUtc);
            return outcomes;
        }
    }

    private static async Task<string> SafeReadAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            return body.Length > 300 ? body[..300] : body;
        }
        catch (Exception ex) { return $"(본문을 읽지 못했습니다: {ex.Message})"; }
    }
}
