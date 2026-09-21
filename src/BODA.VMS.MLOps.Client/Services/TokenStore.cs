using System.Security.Claims;
using System.Text.Json;
using Blazored.LocalStorage;
using Microsoft.AspNetCore.Components.Authorization;

namespace BODA.VMS.MLOps.Client.Services;

/// <summary>
/// 사용자 JWT 보관. BODA.VMS.Web 과 같은 키·발급자를 쓰므로 그 쪽 로그인 토큰을 그대로 붙여넣어도 된다.
/// 워커 토큰(wk_)은 서비스 계정이라 화면에서 쓰지 않는다.
/// </summary>
public sealed class TokenStore(ILocalStorageService storage)
{
    private const string Key = "mlops.token";

    /// <summary>
    /// 운영 웹의 refresh 토큰. 로그인할 때 "로그인 유지" 를 켠 경우에만 생긴다.
    /// 액세스 토큰이 만료되면(기본 8시간) 이것으로 조용히 새로 받는다.
    /// </summary>
    private const string RefreshKey = "mlops.refresh";

    private string? _cached;

    public async Task<string?> GetAsync()
    {
        if (_cached is not null) return _cached;
        _cached = await storage.GetItemAsStringAsync(Key);
        return string.IsNullOrWhiteSpace(_cached) ? _cached = null : _cached;
    }

    public async Task SetAsync(string token)
    {
        _cached = token.Trim();
        await storage.SetItemAsStringAsync(Key, _cached);
    }

    public async Task<string?> GetRefreshAsync()
    {
        var value = await storage.GetItemAsStringAsync(RefreshKey);
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    public Task SetRefreshAsync(string? token) =>
        string.IsNullOrWhiteSpace(token)
            ? storage.RemoveItemAsync(RefreshKey).AsTask()
            : storage.SetItemAsStringAsync(RefreshKey, token.Trim()).AsTask();

    public async Task ClearAsync()
    {
        _cached = null;
        await storage.RemoveItemAsync(Key);
        await storage.RemoveItemAsync(RefreshKey);
    }
}

/// <summary>JWT 를 그대로 읽어 인증 상태를 만든다. 만료된 토큰은 익명으로 취급하고 지운다.</summary>
public sealed class JwtAuthenticationStateProvider(TokenStore tokens, WebLogin webLogin) : AuthenticationStateProvider
{
    private static readonly AuthenticationState Anonymous = new(new ClaimsPrincipal(new ClaimsIdentity()));

    /// <summary>
    /// 갱신은 한 번만 시도한다. 실패한 refresh 토큰으로 화면을 그릴 때마다 운영 웹을 두드리면
    /// 그쪽 갱신 예산(분당 60회)을 혼자 태운다.
    /// </summary>
    private bool _refreshTried;

    public override async Task<AuthenticationState> GetAuthenticationStateAsync()
    {
        var token = await tokens.GetAsync();
        var principal = token is null ? null : Parse(token);

        // 토큰이 없거나 만료됐고 "로그인 유지" 를 켜 뒀다면, 로그인 화면을 보여 주기 전에 한 번 갱신해 본다.
        if (principal is null && !_refreshTried)
        {
            _refreshTried = true;
            var refresh = await tokens.GetRefreshAsync();
            if (refresh is not null)
            {
                var renewed = await webLogin.RefreshAsync(refresh);
                if (renewed is not null)
                {
                    await tokens.SetAsync(renewed.Token);
                    // 운영 웹은 갱신할 때 refresh 토큰을 돌려 끼운다(로테이션). 새것을 받으면 바꿔 둔다.
                    if (!string.IsNullOrWhiteSpace(renewed.RefreshToken))
                        await tokens.SetRefreshAsync(renewed.RefreshToken);
                    principal = Parse(renewed.Token);
                }
            }
        }

        if (principal is null)
        {
            if (token is not null) await tokens.ClearAsync();
            return Anonymous;
        }
        return new AuthenticationState(principal);
    }

    public async Task SignInAsync(string token)
    {
        _refreshTried = false;
        await tokens.SetAsync(token);
        NotifyAuthenticationStateChanged(GetAuthenticationStateAsync());
    }

    public async Task SignOutAsync()
    {
        await tokens.ClearAsync();
        NotifyAuthenticationStateChanged(Task.FromResult(Anonymous));
    }

    /// <summary>
    /// 서명은 서버가 검증한다. 화면은 만료와 역할만 알면 되므로 페이로드만 읽는다.
    /// (System.IdentityModel.Tokens.Jwt 를 끌어오면 WASM 다운로드가 커진다.)
    /// </summary>
    public static ClaimsPrincipal? Parse(string token)
    {
        try
        {
            var parts = token.Split('.');
            if (parts.Length < 2) return null;

            using var doc = JsonDocument.Parse(Base64UrlDecode(parts[1]));
            var payload = doc.RootElement;

            if (payload.TryGetProperty("exp", out var exp) && exp.TryGetInt64(out var seconds)
                && DateTimeOffset.FromUnixTimeSeconds(seconds) < DateTimeOffset.UtcNow.AddSeconds(-30))
                return null;

            var identity = new ClaimsIdentity("jwt", ClaimTypes.Name, ClaimTypes.Role);
            identity.AddClaim(new Claim(ClaimTypes.Name, ReadName(payload)));
            foreach (var role in ReadRoles(payload)) identity.AddClaim(new Claim(ClaimTypes.Role, role));
            return new ClaimsPrincipal(identity);
        }
        catch (Exception ex) when (ex is JsonException or FormatException or ArgumentException)
        {
            return null;
        }
    }

    private static string ReadName(JsonElement payload)
    {
        // 발급자마다 이름 클레임이 달라 흔한 것부터 찾는다
        foreach (var key in new[] { "name", "unique_name", ClaimTypes.Name, "sub", ClaimTypes.NameIdentifier })
            if (payload.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String)
                return v.GetString()!;
        return "user";
    }

    private static IEnumerable<string> ReadRoles(JsonElement payload)
    {
        foreach (var key in new[] { ClaimTypes.Role, "role", "roles" })
        {
            if (!payload.TryGetProperty(key, out var v)) continue;
            if (v.ValueKind == JsonValueKind.String) yield return v.GetString()!;
            else if (v.ValueKind == JsonValueKind.Array)
                foreach (var item in v.EnumerateArray())
                    if (item.ValueKind == JsonValueKind.String) yield return item.GetString()!;
        }
    }

    private static byte[] Base64UrlDecode(string value)
    {
        var s = value.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(s.PadRight(s.Length + (4 - s.Length % 4) % 4, '='));
    }
}

/// <summary>모든 API 요청에 Bearer 토큰을 붙인다</summary>
public sealed class AuthTokenHandler(TokenStore tokens) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var token = await tokens.GetAsync();
        if (token is not null)
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        return await base.SendAsync(request, ct);
    }
}
