using System.Security.Claims;
using System.Text.Encodings.Web;
using BODA.VMS.MLOps.Core.Hashing;
using BODA.VMS.MLOps.Server.Data;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace BODA.VMS.MLOps.Server.Auth;

/// <summary>
/// 워커 서비스 계정 토큰 (Phase 3 §4, §8): <c>Authorization: Bearer wk_…</c>. 서버에는 SHA-256 해시만 저장.
/// 범위는 Worker 정책이 허용하는 API(워커 API·데이터셋 export·사전학습 미러·아티팩트 업로드)로 한정된다.
/// </summary>
public sealed class WorkerTokenAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder, MlopsDbContext db)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "WorkerToken";
    public const string TokenPrefix = "wk_";

    public static bool LooksLikeWorkerToken(string? authorization) =>
        authorization is not null
        && authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
        && authorization.AsSpan(7).TrimStart().StartsWith(TokenPrefix, StringComparison.Ordinal);

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var auth = Request.Headers.Authorization.ToString();
        if (!LooksLikeWorkerToken(auth)) return AuthenticateResult.NoResult();

        var token = auth[7..].Trim();
        var hash = Sha256Util.HashString(token);
        var worker = await db.Workers.AsNoTracking().FirstOrDefaultAsync(w => w.TokenHash == hash, Context.RequestAborted);
        if (worker is null) return AuthenticateResult.Fail("알 수 없는 워커 토큰");

        // 관리자가 비활성한 워커는 인증 단계에서 막는다. 배정 시점에만 막으면 그 토큰으로
        // 데이터셋 export·사전학습 미러·버전 등록이 계속 되어 "이 PC 를 끊었다" 는 조치가 성립하지 않는다.
        // 진단 실패로 인한 Disabled 는 여기서 막지 않는다 — 워커가 재등록으로 스스로 복구해야 하기 때문이다.
        if (worker.AdminDisabled)
            return AuthenticateResult.Fail($"비활성화된 워커입니다: {worker.DisabledReason ?? worker.Name}");

        var identity = new ClaimsIdentity(
        [
            new Claim(ClaimTypes.Name, $"worker:{worker.Name}"),
            new Claim(ClaimTypes.NameIdentifier, worker.Id.ToString()),
            new Claim(ClaimTypes.Role, Roles.Worker),
            new Claim(CurrentUser.WorkerIdClaim, worker.Id.ToString()),
        ], SchemeName);
        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName));
    }
}

/// <summary>Authorization 헤더 접두사로 JWT / 워커 토큰 스킴을 고르는 정책 스킴</summary>
public static class SmartAuthScheme
{
    public const string Name = "Smart";

    public static string Select(HttpContext ctx) =>
        WorkerTokenAuthenticationHandler.LooksLikeWorkerToken(ctx.Request.Headers.Authorization.ToString())
            ? WorkerTokenAuthenticationHandler.SchemeName
            : JwtBearerDefaults.AuthenticationScheme;
}
