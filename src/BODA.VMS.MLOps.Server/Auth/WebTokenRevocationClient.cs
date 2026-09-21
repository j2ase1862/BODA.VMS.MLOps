using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

namespace BODA.VMS.MLOps.Server.Auth;

/// <summary>
/// 운영 웹에서 이미 끊긴 토큰을 걸러낸다.
///
/// <para>
/// <b>왜.</b> 운영 웹은 토큰에 세대(<c>tv</c>)를 싣고 요청마다 DB 와 대조해, 로그아웃·비밀번호 변경·
/// 계정 삭제 같은 "전부 끊어야 하는 사건" 뒤의 토큰을 거부한다. 우리는 서명과 만료만 봤기 때문에
/// <b>그쪽에서 잘린 계정의 토큰이 여기서는 최대 8시간 더 살아 있었다</b> — 사고 대응(탈취 계정 차단)이
/// 절반만 듣는 셈이었다.
/// </para>
/// <para>
/// <b>어떻게.</b> 그 판단은 운영 웹의 DB 가 쥐고 있으므로 우리가 흉내 내지 않는다. 받은 토큰을 그대로
/// 운영 웹의 <c>/api/auth/me</c> 에 들려 보내 <b>401 이면 끊긴 것</b>으로 본다. 운영 웹 코드는 건드리지 않는다.
/// </para>
/// <para>
/// <b>캐시를 두는 이유.</b> 운영 웹은 로컬 DB 단건 조회라 캐시 없이 요청마다 본다. 우리는 네트워크를
/// 건너야 해서 요청마다 부르면 화면 한 번에 수십 번이 된다. 그래서 <see cref="AuthOptions.RevocationCheckSeconds"/>
/// 만큼만 기억한다 — 끊긴 토큰이 살아 있는 창이 8시간에서 그 초만큼으로 줄어든다. 더 조이려면 값을 줄인다.
/// </para>
/// <para>
/// <b>운영 웹이 죽으면 통과시킨다.</b> 이 플랫폼은 운영 웹이 멈춰도 라벨링과 학습은 돌아야 한다
/// (모니터링만 "볼 수 없음" 이 되는 것과 같은 규칙이다). 못 물어봤다는 이유로 전원을 쫓아내지 않는다 —
/// 대신 경고를 남긴다. 401 을 <b>받았을 때만</b> 끊는다.
/// </para>
/// </summary>
public sealed class WebTokenRevocationClient(
    HttpClient http,
    IMemoryCache cache,
    IOptions<AuthOptions> auth,
    IOptions<MonitoringOptions> monitoring,
    ILogger<WebTokenRevocationClient> log)
{
    /// <summary>검사할 수 없는 구성이면 검사 자체를 건너뛴다.</summary>
    public bool Enabled =>
        auth.Value.RevocationCheckSeconds > 0 && !string.IsNullOrWhiteSpace(WebBaseUrl);

    private string WebBaseUrl => string.IsNullOrWhiteSpace(auth.Value.WebBaseUrl)
        ? monitoring.Value.ProductionWebUrl ?? ""
        : auth.Value.WebBaseUrl;

    /// <summary>토큰 원문을 키로 두지 않는다 — 메모리 캐시라도 그대로 들고 있을 이유가 없다.</summary>
    private static string Key(string token) =>
        "rev:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)))[..32];

    public async Task<bool> IsStillValidAsync(string token, CancellationToken ct)
    {
        if (!Enabled) return true;
        if (cache.TryGetValue<bool>(Key(token), out var known)) return known;

        var url = WebBaseUrl.TrimEnd('/') + "/api/auth/me";
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var res = await http.SendAsync(req, ct);

            // 401 만 "끊겼다" 로 읽는다. 403 은 그 엔드포인트의 인가 판단이고(임시 비밀번호 등),
            // 5xx 는 그쪽 사정이다 — 둘 다 토큰이 죽었다는 뜻이 아니다.
            if (res.StatusCode == HttpStatusCode.Unauthorized)
            {
                cache.Set(Key(token), false, TimeSpan.FromSeconds(auth.Value.RevocationCheckSeconds));
                return false;
            }

            if (res.IsSuccessStatusCode)
            {
                cache.Set(Key(token), true, TimeSpan.FromSeconds(auth.Value.RevocationCheckSeconds));
                return true;
            }

            // 뜻을 모르는 응답은 기억하지 않는다. 다음 요청에 다시 물어본다.
            log.LogWarning("운영 웹의 토큰 확인이 {Status} 를 냈습니다 — 통과시킵니다.", (int)res.StatusCode);
            return true;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            log.LogWarning(ex, "운영 웹에 토큰을 확인하지 못했습니다 — 통과시킵니다.");
            return true;
        }
    }
}
