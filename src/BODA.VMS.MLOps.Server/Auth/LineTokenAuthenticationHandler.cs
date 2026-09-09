using System.Security.Claims;
using System.Text.Encodings.Web;
using BODA.VMS.MLOps.Core.Hashing;
using BODA.VMS.MLOps.Server.Data;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace BODA.VMS.MLOps.Server.Auth;

/// <summary>
/// 라인 PC 서비스 계정 토큰: <c>Authorization: Bearer ln_…</c>. 서버에는 SHA-256 해시만 저장한다.
///
/// <para>
/// 워커 토큰과 같은 방식이지만 범위가 다르다. 라인 PC 가 하는 일은 둘뿐이라
/// <see cref="Policies.Line"/> 이 허용하는 것 — 모델 참조 해석·아티팩트 내려받기·NG 사진 올리기 — 만 열린다.
/// 학습 큐와 아티팩트 업로드는 워커 토큰의 몫으로 남는다.
/// </para>
/// <para>
/// 라인 계정에는 <see cref="CurrentUser.LineIdClaim"/> 이 붙는다. 어느 라인이 무엇을 받아 갔는지
/// 감사 로그로 남기기 위해서다.
/// </para>
/// </summary>
public sealed class LineTokenAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder,
    MlopsDbContext db, TimeProvider clock)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "LineToken";
    public const string TokenPrefix = "ln_";

    /// <summary>마지막 사용 시각을 매 요청마다 쓰면 라인 수만큼 쓰기가 늘어난다. 이 간격으로만 갱신한다.</summary>
    private static readonly TimeSpan LastSeenGranularity = TimeSpan.FromMinutes(5);

    public static bool LooksLikeLineToken(string? authorization) =>
        authorization is not null
        && authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
        && authorization.AsSpan(7).TrimStart().StartsWith(TokenPrefix, StringComparison.Ordinal);

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var auth = Request.Headers.Authorization.ToString();
        if (!LooksLikeLineToken(auth)) return AuthenticateResult.NoResult();

        var token = auth[7..].Trim();
        var hash = Sha256Util.HashString(token);
        var line = await db.LineClients.FirstOrDefaultAsync(c => c.TokenHash == hash, Context.RequestAborted);
        if (line is null) return AuthenticateResult.Fail("알 수 없는 라인 토큰");

        if (line.Disabled)
            return AuthenticateResult.Fail($"비활성화된 라인 계정입니다: {line.DisabledReason ?? line.Name}");

        var now = clock.GetUtcNow().UtcDateTime;
        if (line.LastSeenAt is null || now - line.LastSeenAt.Value > LastSeenGranularity)
        {
            line.LastSeenAt = now;
            try { await db.SaveChangesAsync(Context.RequestAborted); }
            catch (DbUpdateException) { /* 마지막 사용 시각은 못 남겨도 요청은 통과해야 한다 */ }
        }

        var identity = new ClaimsIdentity(
        [
            new Claim(ClaimTypes.Name, $"line:{line.Name}"),
            new Claim(ClaimTypes.NameIdentifier, line.Id.ToString()),
            new Claim(ClaimTypes.Role, Roles.Line),
            new Claim(CurrentUser.LineIdClaim, line.LineId),
        ], SchemeName);
        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName));
    }
}
