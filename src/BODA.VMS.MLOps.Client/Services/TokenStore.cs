using System.Security.Claims;
using BODA.VMS.MLOps.Core.Auth;
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

    public async Task ClearAsync()
    {
        _cached = null;
        await storage.RemoveItemAsync(Key);
    }
}

/// <summary>JWT 를 그대로 읽어 인증 상태를 만든다. 만료된 토큰은 익명으로 취급하고 지운다.</summary>
public sealed class JwtAuthenticationStateProvider(TokenStore tokens) : AuthenticationStateProvider
{
    private static readonly AuthenticationState Anonymous = new(new ClaimsPrincipal(new ClaimsIdentity()));

    public override async Task<AuthenticationState> GetAuthenticationStateAsync()
    {
        var token = await tokens.GetAsync();
        if (token is null) return Anonymous;

        var principal = Parse(token);
        if (principal is null)
        {
            await tokens.ClearAsync();
            return Anonymous;
        }
        return new AuthenticationState(principal);
    }

    public async Task SignInAsync(string token)
    {
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
            // 운영 웹 역할을 서버와 같은 규칙으로 옮긴다. 안 옮기면 권한은 있는데 버튼이 안 보인다.
            foreach (var role in WebRoleMapping.Expand(ReadRoles(payload))) identity.AddClaim(new Claim(ClaimTypes.Role, role));
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
