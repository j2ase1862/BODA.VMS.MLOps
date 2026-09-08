using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using BODA.VMS.MLOps.Server.Auth;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace BODA.VMS.MLOps.Server.Endpoints;

public sealed record DevTokenRequest(string User, string[] Roles, int Hours = 8);

public static class AuthEndpoints
{
    public static RouteGroupBuilder MapAuthEndpoints(this RouteGroupBuilder api)
    {
        var g = api.MapGroup("/auth").WithTags("Auth");

        /// 운영에서는 BODA.VMS.Web 의 로그인 JWT 를 그대로 쓴다 (같은 Jwt:Key/Issuer/Audience).
        /// 이 엔드포인트는 Auth:EnableDevTokens=true 일 때만 열리는 개발·테스트용 발급기다.
        g.MapPost("/dev-token", (DevTokenRequest req, IOptions<JwtOptions> jwt, IOptions<AuthOptions> auth) =>
        {
            if (!auth.Value.EnableDevTokens) return Results.NotFound();
            if (string.IsNullOrWhiteSpace(req.User)) return Results.BadRequest(new { error = "user 필수" });
            var roles = (req.Roles ?? []).Where(r => Roles.All.Contains(r, StringComparer.OrdinalIgnoreCase)).ToArray();

            var claims = new List<Claim>
            {
                new(JwtRegisteredClaimNames.Sub, req.User),
                new(ClaimTypes.Name, req.User),
                new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString("N")),
            };
            claims.AddRange(roles.Select(r => new Claim(ClaimTypes.Role, r)));

            var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwt.Value.Key));
            // JwtBearer 검증(nbf/exp)은 실제 시스템 시각을 쓰므로 발급도 실제 시각으로 한다.
            // 주입된 TimeProvider(테스트의 가짜 시계)를 쓰면 미래 시각 토큰이 되어 401 이 난다.
            var now = DateTime.UtcNow;
            var token = new JwtSecurityToken(jwt.Value.Issuer, jwt.Value.Audience, claims, now, now.AddHours(Math.Clamp(req.Hours, 1, 24 * 30)),
                new SigningCredentials(key, SecurityAlgorithms.HmacSha256));
            return Results.Ok(new { token = new JwtSecurityTokenHandler().WriteToken(token), roles });
        }).AllowAnonymous();

        g.MapGet("/me", (ClaimsPrincipal principal) =>
        {
            var u = CurrentUser.From(principal);
            return Results.Ok(new { u.Name, roles = u.RolesSet.ToArray(), u.WorkerId });
        }).RequireAuthorization(Policies.Viewer);

        return api;
    }
}
