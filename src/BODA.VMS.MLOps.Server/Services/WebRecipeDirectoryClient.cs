using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using BODA.VMS.MLOps.Contracts.Models;
using BODA.VMS.MLOps.Server.Auth;
using Microsoft.Extensions.Options;

namespace BODA.VMS.MLOps.Server.Services;

/// <summary>
/// 운영 웹(BODA.VMS.Web)의 레시피 목록 — [레시피 바인딩] 이 레시피 ID 를 손으로 붙여 넣지 않고 고르게 한다 (ML-02).
///
/// <para><b>읽기만 한다.</b> 그쪽의 <c>GET /api/clients</c> 와 <c>GET /api/clients/{id}/recipes</c> 를
/// <b>부른 사람의 토큰 그대로</b> 들려 부른다(<see cref="WebAccountDirectoryClient"/> 와 같은 길). 운영 웹 코드는 건드리지 않고,
/// 그쪽에 쓰기 경로도 만들지 않는다 — 모니터링과 같은 "당겨 오기만" 규칙이다.</para>
///
/// <para>실패해도 예외를 내지 않는다. 목록은 편의일 뿐이고, 바인딩은 ID 를 직접 넣어서도 된다.
/// 도구 목록은 운영 웹에 없다(Camera·Step·Tool 은 VisionServer 소유) — 도구 ID 는 직접 넣는다.</para>
/// </summary>
public sealed class WebRecipeDirectoryClient(
    HttpClient http,
    IOptions<AuthOptions> auth,
    IOptions<MonitoringOptions> monitoring,
    ILogger<WebRecipeDirectoryClient> log)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private string WebBaseUrl => string.IsNullOrWhiteSpace(auth.Value.WebBaseUrl)
        ? monitoring.Value.ProductionWebUrl ?? ""
        : auth.Value.WebBaseUrl;

    public async Task<WebRecipeListDto> ListAsync(ClaimsPrincipal caller, string? authorizationHeader, CancellationToken ct)
    {
        if (auth.Value.Mode == AuthMode.Local)
            return Unavailable("운영 웹 없이 쓰는 설치라 레시피 목록을 받을 수 없습니다. 레시피 ID 를 직접 넣으세요.");
        if (string.IsNullOrWhiteSpace(WebBaseUrl))
            return Unavailable("이 서버에 운영 웹 주소가 설정되어 있지 않아 레시피 목록을 받을 수 없습니다. 레시피 ID 를 직접 넣으세요.");
        // 개발용·서비스 토큰은 우리가 만든 것이라 운영 웹이 모른다
        if (caller.HasClaim(ServiceTokenIssuer.SelfIssuedClaim, "1")
            || authorizationHeader is null || !authorizationHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            return Unavailable("운영 웹 계정으로 로그인한 것이 아니라 레시피 목록을 받을 수 없습니다. 레시피 ID 를 직접 넣으세요.");

        var baseUrl = WebBaseUrl.TrimEnd('/');
        try
        {
            var clients = await GetAsync<List<WebClientRow>>($"{baseUrl}/api/clients", authorizationHeader, ct);
            if (clients is null) return Unavailable("운영 웹이 라인 목록을 내주지 않았습니다. 레시피 ID 를 직접 넣으세요.");

            var recipes = new List<WebRecipeDto>();
            foreach (var client in clients.OrderBy(c => c.ClientIndex))
            {
                var rows = await GetAsync<List<WebRecipeRow>>($"{baseUrl}/api/clients/{client.Id}/recipes", authorizationHeader, ct);
                if (rows is null) continue;   // 한 라인이 실패해도 나머지는 보여 준다
                recipes.AddRange(rows.Where(r => r.Id > 0).Select(r => new WebRecipeDto(
                    r.Id.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    string.IsNullOrWhiteSpace(r.Name) ? $"레시피 {r.Id}" : r.Name.Trim(),
                    client.Id, string.IsNullOrWhiteSpace(client.Name) ? $"라인 {client.ClientIndex}" : client.Name.Trim())));
            }
            return new WebRecipeListDto(true, null, recipes);
        }
        catch (WebDirectoryException ex)
        {
            return Unavailable(ex.Message);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or NotSupportedException)
        {
            log.LogWarning(ex, "운영 웹 레시피 목록을 받지 못했습니다 ({Url})", baseUrl);
            return Unavailable("운영 웹에 연결하지 못해 레시피 목록을 받을 수 없습니다. 레시피 ID 를 직접 넣으세요.");
        }
    }

    /// <summary>성공이면 본문, 404 면 null. 401·403·그 밖은 이유를 담아 던진다.</summary>
    private async Task<T?> GetAsync<T>(string url, string authorizationHeader, CancellationToken ct) where T : class
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Authorization = AuthenticationHeaderValue.Parse(authorizationHeader);
        using var res = await http.SendAsync(req, ct);
        if (res.StatusCode == HttpStatusCode.NotFound) return null;
        if (res.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            throw new WebDirectoryException("운영 웹이 이 계정에 레시피 목록을 내주지 않았습니다. 레시피 ID 를 직접 넣으세요.");
        if (!res.IsSuccessStatusCode)
        {
            log.LogWarning("운영 웹 {Url} 이 {Status} 를 냈습니다", url, (int)res.StatusCode);
            throw new WebDirectoryException($"운영 웹이 레시피 목록을 내주지 않았습니다 ({(int)res.StatusCode}). 레시피 ID 를 직접 넣으세요.");
        }
        return await res.Content.ReadFromJsonAsync<T>(Json, ct);
    }

    private static WebRecipeListDto Unavailable(string message) => new(false, message, []);

    private sealed class WebDirectoryException(string message) : Exception(message);

    /// <summary>운영 웹 ClientDto 의 필요한 칸만</summary>
    private sealed record WebClientRow(int Id, string? Name, int ClientIndex);

    /// <summary>운영 웹 RecipeDto 의 필요한 칸만</summary>
    private sealed record WebRecipeRow(int Id, string? Name);
}
