using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using BODA.VMS.MLOps.Server.Data.Entities;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace BODA.VMS.MLOps.Server.Auth;

/// <summary>
/// 자체 계정 로그인(<see cref="AuthMode.Local"/>)의 토큰을 만든다.
///
/// <para>
/// <b>여기서 만든 토큰에는 <see cref="ServiceTokenIssuer.SelfIssuedClaim"/> 을 붙이지 않는다.</b>
/// 그 표시가 붙으면 <see cref="MemberRoleClaimsTransformation"/> 이 역할 표를 적용하지 않아,
/// 표에서 강등하거나 계정을 비활성해도 토큰 수명 동안 듣지 않는다. 로그인 토큰은 표가 정본이어야 한다.
/// </para>
/// <para>
/// <b>대신 발급자로 가른다.</b> <see cref="WebTokenRevocationClient"/> 는 받은 토큰을 운영 웹에
/// 물어 401 이면 끊는데, 운영 웹은 우리 로컬 계정을 모르므로 무조건 401 이다. 그 검사가
/// <c>Monitoring:ProductionWebUrl</c> 만 있으면 켜지기 때문에, 가르지 않으면 로컬 모드에서
/// 모니터링을 쓰는 순간 로그인한 사람이 전원 쫓겨난다.
/// </para>
/// <para>
/// 토큰에 역할을 싣는 것은 <b>화면을 위해서</b>다 (브라우저는 토큰만 보고 메뉴를 그린다).
/// 서버의 판단에는 쓰이지 않는다 — 변환이 실린 역할을 지우고 표의 역할로 바꾼다.
/// </para>
/// </summary>
public sealed class LocalTokenIssuer(IOptions<AuthOptions> auth)
{
    /// <summary>토큰 세대(<see cref="Member.SecurityStamp"/>). 표의 값과 다르면 그 토큰은 죽은 것으로 본다.</summary>
    public const string PasswordStampClaim = "mlops:pwstamp";

    private LocalAuthOptions Local => auth.Value.Local;

    public string Issue(Member member)
    {
        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, member.Username),
            new(ClaimTypes.Name, member.Username),
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString("N")),
            new(PasswordStampClaim, Stamp(member.SecurityStamp)),
        };
        if (!string.IsNullOrWhiteSpace(member.DisplayName)) claims.Add(new Claim("DisplayName", member.DisplayName));
        // 화면용. 서버는 이 값을 믿지 않는다 (위 주석).
        if (!string.IsNullOrWhiteSpace(member.Role) && !member.MustChangePassword)
            claims.Add(new Claim(ClaimTypes.Role, member.Role));

        return Write(claims, Local.TokenHours);
    }

    /// <summary>
    /// 로컬 모드의 개발 토큰. 운영 웹이 없는 서버에서도 <c>Auth:EnableDevTokens</c> 가 쓰일 수 있어야 한다.
    /// 이쪽은 실린 역할이 곧 의도이므로 자체 발급 표시를 붙인다 (역할 표를 보지 않는다).
    /// </summary>
    public string IssueService(string user, IEnumerable<string> roles, int hours)
    {
        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, user),
            new(ClaimTypes.Name, user),
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString("N")),
            new(ServiceTokenIssuer.SelfIssuedClaim, "1"),
        };
        claims.AddRange(ServiceTokenIssuer.AcceptedRoles(roles).Select(r => new Claim(ClaimTypes.Role, r)));
        return Write(claims, hours);
    }

    /// <summary>토큰에 실린 것과 표의 것을 견주기 위한 문자열. null(비밀번호가 없는 줄)도 값이다.</summary>
    public static string Stamp(string? securityStamp) => securityStamp ?? "0";

    private string Write(List<Claim> claims, int hours)
    {
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(Local.Key));

        // JwtBearer 검증(nbf/exp)은 실제 시스템 시각을 본다. 테스트의 가짜 시계로 발급하면 401 이다.
        var now = DateTime.UtcNow;
        var token = new JwtSecurityToken(
            Local.Issuer, Local.Audience, claims,
            now, now.AddHours(Math.Clamp(hours, 1, 24 * 30)),
            new SigningCredentials(key, SecurityAlgorithms.HmacSha256));

        return new JwtSecurityTokenHandler().WriteToken(token);
    }
}
