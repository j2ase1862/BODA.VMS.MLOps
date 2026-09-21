using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using BODA.VMS.MLOps.Contracts.Auth;

namespace BODA.VMS.MLOps.Client.Services;

/// <summary>운영 웹 로그인 응답 — 그쪽 <c>LoginResponse</c> 중 우리가 쓰는 것만.</summary>
public sealed class WebLoginResult
{
    public string Token { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string RefreshToken { get; set; } = "";
    public DateTime AccessTokenExpiresAt { get; set; }
    public bool MustChangePassword { get; set; }
}

/// <summary>로그인이 왜 막혔는지. 화면이 사람에게 할 말을 고르는 데 쓴다.</summary>
public enum WebLoginFailure
{
    None,
    /// <summary>아이디나 비밀번호가 다름.</summary>
    BadCredentials,
    /// <summary>운영 웹이 시도 횟수를 제한했다 (IP 당 5회/분).</summary>
    TooManyAttempts,
    /// <summary>임시 비밀번호 상태 — 운영 웹에서 바꾸고 와야 한다.</summary>
    MustChangePassword,
    /// <summary>브라우저가 운영 웹에 닿지 못했다. CORS 거부와 네트워크 단절이 여기서 구분되지 않는다.</summary>
    Unreachable,
    /// <summary>운영 웹 주소가 설정되지 않았다.</summary>
    NotConfigured,
    Other,
}

public sealed record WebLoginOutcome(WebLoginResult? Result, WebLoginFailure Failure, string? Detail = null)
{
    public bool Ok => Result is not null && Failure == WebLoginFailure.None;
}

/// <summary>
/// 운영 웹(BODA.VMS.Web)에 로그인한다.
///
/// <para>
/// <b>브라우저가 직접 부른다.</b> 서버가 대신 부르면 비밀번호가 우리 서버를 지나가고, 더 나쁘게는
/// 운영 웹의 로그인 제한(IP 당 5회/분)을 공장 전체가 나눠 쓰게 된다 — 아침에 여럿이 들어오면
/// 정상 사용만으로 429 다. 직접 부르면 그 예산이 사람마다 따로 잡힌다.
/// </para>
/// <para>
/// 대신 오리진이 달라지므로 운영 웹의 <c>Cors:AllowedOrigins</c> 에 이 서버 주소가 있어야 한다.
/// 빠졌을 때 브라우저는 네트워크 단절과 똑같은 오류만 주므로, 실패하면 우리 서버에
/// <c>/api/auth/web-reachable</c> 을 물어 둘을 갈라 준다.
/// </para>
/// </summary>
public sealed class WebLogin(MlopsApi api)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private string? _webBaseUrl;

    /// <summary>운영 웹 주소를 서버에서 받아 온다. 비어 있으면 아이디·비밀번호 로그인을 쓸 수 없다.</summary>
    public async Task<string?> WebBaseUrlAsync()
    {
        if (_webBaseUrl is not null) return _webBaseUrl.Length == 0 ? null : _webBaseUrl;
        try
        {
            var info = await api.WebLoginInfoAsync();
            _webBaseUrl = (info.WebBaseUrl ?? "").TrimEnd('/');
        }
        catch (MlopsApiException) { _webBaseUrl = ""; }
        catch (HttpRequestException) { _webBaseUrl = ""; }
        return _webBaseUrl.Length == 0 ? null : _webBaseUrl;
    }

    public async Task<WebLoginOutcome> LoginAsync(string username, string password, bool rememberMe)
    {
        var baseUrl = await WebBaseUrlAsync();
        if (baseUrl is null) return new WebLoginOutcome(null, WebLoginFailure.NotConfigured);

        // 우리 API 용 HttpClient 를 쓰지 않는다 — 그쪽에는 우리 토큰을 붙이는 핸들러가 달려 있고
        // 주소도 우리 오리진이다.
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        try
        {
            using var res = await http.PostAsJsonAsync($"{baseUrl}/api/auth/login",
                new { username, password, rememberMe }, Json);

            if (res.StatusCode == HttpStatusCode.Unauthorized)
                return new WebLoginOutcome(null, WebLoginFailure.BadCredentials);
            if (res.StatusCode == HttpStatusCode.TooManyRequests)
                return new WebLoginOutcome(null, WebLoginFailure.TooManyAttempts);
            if (!res.IsSuccessStatusCode)
                return new WebLoginOutcome(null, WebLoginFailure.Other, $"HTTP {(int)res.StatusCode}");

            var body = await res.Content.ReadFromJsonAsync<WebLoginResult>(Json);
            if (body is null || string.IsNullOrWhiteSpace(body.Token))
                return new WebLoginOutcome(null, WebLoginFailure.Other, "운영 웹이 토큰을 주지 않았습니다.");

            // 임시 비밀번호는 운영 웹에서만 바꿀 수 있다. 그 상태로 들여보내면 여기서 막다른 길이다.
            if (body.MustChangePassword)
                return new WebLoginOutcome(body, WebLoginFailure.MustChangePassword);

            return new WebLoginOutcome(body, WebLoginFailure.None);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return new WebLoginOutcome(null, WebLoginFailure.Unreachable, ex.Message);
        }
    }

    /// <summary>
    /// 저장해 둔 refresh 토큰으로 새 액세스 토큰을 받는다. "로그인 유지" 를 켰을 때만 토큰이 있다.
    /// 실패는 조용히 없는 것으로 친다 — 그러면 로그인 화면이 뜬다.
    /// </summary>
    public async Task<WebLoginResult?> RefreshAsync(string refreshToken)
    {
        var baseUrl = await WebBaseUrlAsync();
        if (baseUrl is null || string.IsNullOrWhiteSpace(refreshToken)) return null;

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        try
        {
            using var res = await http.PostAsJsonAsync($"{baseUrl}/api/auth/refresh", new { refreshToken }, Json);
            if (!res.IsSuccessStatusCode) return null;
            var body = await res.Content.ReadFromJsonAsync<WebLoginResult>(Json);
            return string.IsNullOrWhiteSpace(body?.Token) ? null : body;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return null;
        }
    }

    /// <summary>브라우저만 막힌 것인지(대개 CORS) 네트워크가 끊긴 것인지 서버에 물어본다.</summary>
    public async Task<string> DiagnoseAsync()
    {
        try
        {
            var r = await api.WebReachableAsync();
            return r.Reachable
                ? $"{r.Message} 브라우저만 막힌 것이라, 운영 웹의 Cors:AllowedOrigins 에 이 서버 주소가 들어 있는지 확인하세요."
                : r.Message;
        }
        catch (MlopsApiException ex) { return ex.Describe(); }
        catch (HttpRequestException ex) { return ex.Message; }
    }
}
