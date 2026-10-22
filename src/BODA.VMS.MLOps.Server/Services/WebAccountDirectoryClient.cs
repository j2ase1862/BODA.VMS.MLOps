using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using BODA.VMS.MLOps.Contracts.Members;
using BODA.VMS.MLOps.Server.Auth;
using Microsoft.Extensions.Options;

namespace BODA.VMS.MLOps.Server.Services;

/// <summary>
/// 운영 웹(BODA.VMS.Web)의 계정 목록 — [사용자 역할] 의 [역할 주기] 가 아이디를 손으로 치지 않고 고르게 한다.
/// 손으로 치면 오타가 그대로 저장되고, 그 줄은 아무에게도 맞지 않아 그 사람은 계속 기본 역할로 들어왔다.
///
/// <para><b>어떻게.</b> 그쪽의 <c>GET /api/admin/users</c> 를 <b>부른 사람의 토큰 그대로</b> 들려 부른다.
/// 운영 웹 코드는 건드리지 않는다. 그 API 는 운영 웹 Admin 만 열리므로, MLOps 관리자라도 운영 웹에서
/// Admin 이 아니면 목록을 받지 못한다 — 그때는 이유를 돌려주고 화면은 직접 입력으로 돌아간다.</para>
///
/// <para>실패해도 예외를 내지 않는다. 목록은 편의일 뿐이고, 역할 주기 자체는 막히면 안 된다.</para>
/// </summary>
public sealed class WebAccountDirectoryClient(
    HttpClient http,
    IOptions<AuthOptions> auth,
    IOptions<MonitoringOptions> monitoring,
    ILogger<WebAccountDirectoryClient> log)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private string WebBaseUrl => string.IsNullOrWhiteSpace(auth.Value.WebBaseUrl)
        ? monitoring.Value.ProductionWebUrl ?? ""
        : auth.Value.WebBaseUrl;

    public async Task<WebAccountListDto> ListAsync(ClaimsPrincipal caller, string? authorizationHeader, CancellationToken ct)
    {
        if (auth.Value.Mode == AuthMode.Local)
            return Unavailable(WebAccountUnavailable.LocalMode, "운영 웹 없이 쓰는 설치입니다. 계정 이름을 직접 넣어 계정을 만드세요.");
        if (string.IsNullOrWhiteSpace(WebBaseUrl))
            return Unavailable(WebAccountUnavailable.NotConfigured, "이 서버에 운영 웹 주소가 설정되어 있지 않아 계정 목록을 받을 수 없습니다.");
        // 개발용·서비스 토큰은 우리가 만든 것이라 운영 웹이 모른다
        if (caller.HasClaim(ServiceTokenIssuer.SelfIssuedClaim, "1")
            || authorizationHeader is null || !authorizationHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            return Unavailable(WebAccountUnavailable.NotWebToken, "운영 웹 계정으로 로그인한 것이 아니라 그쪽 계정 목록을 받을 수 없습니다.");

        var url = WebBaseUrl.TrimEnd('/') + "/api/admin/users";
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Authorization = AuthenticationHeaderValue.Parse(authorizationHeader);
            using var res = await http.SendAsync(req, ct);

            if (res.StatusCode == HttpStatusCode.Forbidden)
                return Unavailable(WebAccountUnavailable.NotWebAdmin,
                    "운영 웹 계정 목록은 운영 웹에서 Admin 인 사람만 볼 수 있습니다. 아이디를 직접 넣으세요.");
            if (!res.IsSuccessStatusCode)
            {
                log.LogWarning("운영 웹 계정 목록이 {Status} 를 냈습니다", (int)res.StatusCode);
                return Unavailable(WebAccountUnavailable.Failed, $"운영 웹이 계정 목록을 내주지 않았습니다 ({(int)res.StatusCode}). 아이디를 직접 넣으세요.");
            }

            var rows = await res.Content.ReadFromJsonAsync<List<WebUserRow>>(Json, ct) ?? [];
            var accounts = rows
                .Where(r => !string.IsNullOrWhiteSpace(r.Username))
                .Select(r => new WebAccountDto(r.Username!.Trim(), string.IsNullOrWhiteSpace(r.DisplayName) ? null : r.DisplayName.Trim(),
                    r.Role ?? "", r.IsApproved))
                .OrderBy(a => a.Username, StringComparer.OrdinalIgnoreCase)
                .ToList();
            return new WebAccountListDto(true, null, null, accounts);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or NotSupportedException)
        {
            log.LogWarning(ex, "운영 웹 계정 목록을 받지 못했습니다 ({Url})", url);
            return Unavailable(WebAccountUnavailable.Unreachable, "운영 웹에 연결하지 못해 계정 목록을 받을 수 없습니다. 아이디를 직접 넣으세요.");
        }
    }

    private static WebAccountListDto Unavailable(string reason, string message) => new(false, reason, message, []);

    /// <summary>운영 웹 UserDto 의 필요한 칸만 (Id·CreatedAt 등은 쓰지 않는다)</summary>
    private sealed record WebUserRow(string? Username, string? DisplayName, string? Role, bool IsApproved);
}
