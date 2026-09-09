using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace BODA.VMS.MLOps.Server.Auth;

/// <summary>
/// 서버가 스스로 쓸 JWT 를 만든다.
///
/// <para>
/// 쓰는 곳은 둘이다 — 개발용 토큰 발급 엔드포인트, 그리고 운영 웹(BODA.VMS.Web)에서
/// 검사 결과 집계를 당겨 올 때. 두 서버가 같은 서명 키·발급자를 쓰기 때문에
/// 여기서 만든 토큰이 그쪽에서도 통한다.
/// </para>
/// <para><b>역할은 필요한 만큼만 넣으세요.</b>
/// 이 발급기는 아무 역할이나 넣어 줄 수 있다. 토큰이 새는 순간 그 역할이 그대로 남의 손에
/// 들어가므로, 부르는 쪽이 최소 역할을 고르는 것이 유일한 방어선이다.
/// </para>
/// </summary>
public sealed class ServiceTokenIssuer(IOptions<JwtOptions> jwt)
{
    /// <summary>
    /// 토큰을 만든다. <paramref name="roles"/> 는 <see cref="Roles.All"/> 안의 것만 실린다 —
    /// 오타로 만든 역할이 조용히 실려 나중에 "왜 권한이 없지" 로 헤매지 않게 한다.
    ///
    /// <para>
    /// <paramref name="audience"/> 를 주면 그 값으로 발급한다. <b>남의 서버로 보낼 토큰은
    /// 그쪽이 검증하는 audience 여야 한다</b> — 우리 것과 같다고 보면 401 을 받는다.
    /// 실제로 BODA.VMS.Web 은 <c>BODA.VMS.Web.Client</c> 를 검증한다.
    /// null 이면 우리 자신의 audience 를 쓴다 (개발 토큰 등 우리가 받을 토큰).
    /// </para>
    /// </summary>
    public string Issue(string user, IEnumerable<string> roles, int hours = 1, string? audience = null)
    {
        var accepted = roles.Where(r => Roles.All.Contains(r, StringComparer.OrdinalIgnoreCase)).ToArray();

        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, user),
            new(ClaimTypes.Name, user),
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString("N")),
        };
        claims.AddRange(accepted.Select(r => new Claim(ClaimTypes.Role, r)));

        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwt.Value.Key));

        // JwtBearer 검증(nbf/exp)은 실제 시스템 시각을 쓰므로 발급도 실제 시각으로 한다.
        // 주입된 TimeProvider(테스트의 가짜 시계)를 쓰면 미래 시각 토큰이 되어 401 이 난다.
        var now = DateTime.UtcNow;
        var token = new JwtSecurityToken(
            jwt.Value.Issuer,
            string.IsNullOrWhiteSpace(audience) ? jwt.Value.Audience : audience,
            claims,
            now, now.AddHours(Math.Clamp(hours, 1, 24 * 30)),
            new SigningCredentials(key, SecurityAlgorithms.HmacSha256));

        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    /// <summary>발급 결과에서 실제로 실린 역할 — 개발 토큰 응답이 그대로 돌려준다.</summary>
    public static string[] AcceptedRoles(IEnumerable<string>? roles) =>
        (roles ?? []).Where(r => Roles.All.Contains(r, StringComparer.OrdinalIgnoreCase)).ToArray();
}
