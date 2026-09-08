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

        // 브라우저의 <img src> 는 Authorization 헤더를 붙일 수 없다. 이미지 경로에만 쓰이는
        // 쿠키를 하나 내려, 격자 썸네일과 캔버스가 평범한 img 태그로 그려질 수 있게 한다.
        // 토큰을 URL 에 넣는 방법도 있지만 그러면 접근 로그와 방문 기록에 토큰이 남는다.
        g.MapPost("/image-cookie", (HttpRequest request, HttpResponse response) =>
        {
            var header = request.Headers.Authorization.ToString();
            if (!header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
                throw ApiException.Forbidden("Bearer 토큰이 필요합니다.");

            response.Cookies.Append(ImageCookie.Name, header[7..].Trim(), new CookieOptions
            {
                HttpOnly = true,                        // 스크립트가 읽지 못한다
                Secure = request.IsHttps,
                SameSite = SameSiteMode.Strict,         // 다른 사이트에서 부르는 요청에는 붙지 않는다
                Path = ImageCookie.Path,                // 이미지 경로 밖으로는 나가지 않는다
                // 만료를 따로 두지 않는다. 브라우저를 닫으면 사라지고, 다시 열면 화면이 다시 받아 간다.
                // 시간을 박아 두면 탭을 오래 열어 둔 사람의 이미지가 어느 순간부터 안 보인다.
                // 담긴 토큰 자체가 만료되면 서버가 어차피 거부하므로 수명이 짧아지지는 않는다.
            });
            return Results.NoContent();
        }).RequireAuthorization(Policies.Viewer);

        g.MapPost("/image-cookie/clear", (HttpResponse response) =>
        {
            response.Cookies.Delete(ImageCookie.Name, new CookieOptions { Path = ImageCookie.Path });
            return Results.NoContent();
        }).AllowAnonymous();

        return api;
    }
}

/// <summary>이미지 요청에만 쓰이는 쿠키. 이름과 경로를 한 곳에 모아 서버와 클라이언트가 어긋나지 않게 한다.</summary>
public static class ImageCookie
{
    public const string Name = "mlops.img";
    public const string Path = "/api/images";
}
