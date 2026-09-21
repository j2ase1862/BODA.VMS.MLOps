using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using BODA.VMS.MLOps.Contracts.Auth;
using BODA.VMS.MLOps.Server.Auth;
using BODA.VMS.MLOps.Server.Services;
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
        g.MapPost("/dev-token", (DevTokenRequest req, ServiceTokenIssuer issuer, IOptions<AuthOptions> auth) =>
        {
            if (!auth.Value.EnableDevTokens) return Results.NotFound();
            if (string.IsNullOrWhiteSpace(req.User)) return Results.BadRequest(new { error = "user 필수" });

            var roles = ServiceTokenIssuer.AcceptedRoles(req.Roles);
            return Results.Ok(new { token = issuer.Issue(req.User, roles, req.Hours), roles });
        }).AllowAnonymous();

        // 로그인 화면이 "어디로 로그인하면 되나" 를 묻는다. 로그인하기 전이라 익명이어야 한다.
        // 주소가 비어 있으면 화면은 토큰 붙여넣기만 내놓는다.
        g.MapGet("/web-login", (IOptions<AuthOptions> auth, IOptions<MonitoringOptions> monitoring) =>
            Results.Ok(new WebLoginInfo(WebBase(auth.Value, monitoring.Value))))
            .AllowAnonymous();

        // 브라우저 로그인이 실패했을 때 원인을 갈라 준다. 브라우저에서는 CORS 거부와 네트워크 단절이
        // 똑같이 "Failed to fetch" 로 보이지만, 서버가 그 주소에 닿는지는 우리가 알 수 있다 —
        // 닿는데 브라우저만 막혔다면 십중팔구 운영 웹의 Cors:AllowedOrigins 에 우리 주소가 없는 것이다.
        g.MapGet("/web-reachable", async (IOptions<AuthOptions> auth, IOptions<MonitoringOptions> monitoring,
            IHttpClientFactory factory, CancellationToken ct) =>
        {
            var baseUrl = WebBase(auth.Value, monitoring.Value);
            if (string.IsNullOrWhiteSpace(baseUrl))
                return Results.Ok(new WebReachability(false, "운영 웹 주소가 설정되지 않았습니다."));

            using var http = factory.CreateClient();
            http.Timeout = TimeSpan.FromSeconds(5);
            try
            {
                // 아무 응답이나 오면 닿는 것이다. 401·404 도 "그 서버가 거기 있다" 는 뜻이다.
                using var res = await http.GetAsync(baseUrl.TrimEnd('/') + "/api/auth/me", ct);
                return Results.Ok(new WebReachability(true, $"서버는 운영 웹에 닿습니다 (HTTP {(int)res.StatusCode})."));
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                return Results.Ok(new WebReachability(false, $"서버도 운영 웹에 닿지 못합니다: {ex.Message}"));
            }
        }).AllowAnonymous();

        // 화면이 로그인 직후 한 번 부른다. 역할 표에 있는 사람이면 이때 마지막 접속 시각을 남긴다 —
        // 요청마다 쓰면 목록 화면 한 번에 수십 번이 된다.
        g.MapGet("/me", async (ClaimsPrincipal principal, MemberService members, CancellationToken ct) =>
        {
            var u = CurrentUser.From(principal);
            if (u.WorkerId is null && u.LineId is null)
                await members.TouchAsync(u.Name, principal.FindFirstValue("DisplayName"), ct);
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

    /// <summary>로그인용 운영 웹 주소. 따로 적지 않았으면 모니터링이 쓰는 주소와 같다.</summary>
    private static string WebBase(AuthOptions auth, MonitoringOptions monitoring) =>
        string.IsNullOrWhiteSpace(auth.WebBaseUrl) ? monitoring.ProductionWebUrl ?? "" : auth.WebBaseUrl;
}

/// <summary>이미지 요청에만 쓰이는 쿠키. 이름과 경로를 한 곳에 모아 서버와 클라이언트가 어긋나지 않게 한다.</summary>
public static class ImageCookie
{
    public const string Name = "mlops.img";
    public const string Path = "/api/images";
}
