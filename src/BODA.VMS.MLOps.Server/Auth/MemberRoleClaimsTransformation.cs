using System.Security.Claims;
using BODA.VMS.MLOps.Server.Data;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

namespace BODA.VMS.MLOps.Server.Auth;

/// <summary>
/// 운영 웹이 발급한 토큰의 역할을 우리 역할로 바꾼다. 인가 정책(<see cref="Policies"/>)이
/// 돌기 전에 끼어들어야 하므로 <see cref="IClaimsTransformation"/> 자리에 붙인다.
///
/// <para>
/// 규칙은 셋이고 위에서부터 이긴다.
/// <list type="number">
///   <item>역할 표(<c>Members</c>)에 그 계정이 있으면 그 역할.</item>
///   <item>없는데 토큰이 <see cref="AuthOptions.BootstrapWebRole"/> 을 달고 있으면 Admin —
///         표가 빈 서버에서 첫 역할을 줄 사람이 들어오는 통로다.</item>
///   <item>둘 다 아니면 <see cref="AuthOptions.DefaultRole"/> (기본 Viewer).</item>
/// </list>
/// </para>
/// <para>
/// <b>워커·라인 토큰과 우리가 발급한 토큰은 건드리지 않는다.</b> 전자는 서비스 계정이라 역할이
/// 인증 핸들러에서 이미 정해졌고, 후자(개발 토큰)는 요청한 역할 그대로여야 시험이 성립한다.
/// </para>
/// <para>
/// 이 메서드는 <b>요청마다</b> 불린다. 매번 DB 를 보면 목록 화면 한 번에 수십 번이 되므로
/// 짧게 캐시한다. 역할을 바꾸면 <see cref="Invalidate"/> 로 그 줄만 버린다 —
/// 권한을 거둔 조치가 캐시 때문에 몇 분 늦게 듣는 일은 없어야 한다.
/// </para>
/// </summary>
public sealed class MemberRoleClaimsTransformation(
    IServiceScopeFactory scopes,
    IMemoryCache cache,
    IOptions<AuthOptions> auth) : IClaimsTransformation
{
    /// <summary>캐시 수명. 역할 변경은 즉시 버리므로(<see cref="Invalidate"/>) 길이는 다른 서버에서
    /// DB 를 직접 고친 경우에만 의미가 있다.</summary>
    private static readonly TimeSpan CacheFor = TimeSpan.FromMinutes(2);

    private static string Key(string username) => "member:" + username;

    public static void Invalidate(IMemoryCache cache, string username) =>
        cache.Remove(Key(Data.Entities.Member.Normalize(username)));

    public async Task<ClaimsPrincipal> TransformAsync(ClaimsPrincipal principal)
    {
        var identity = principal.Identity as ClaimsIdentity;
        if (identity is null || !identity.IsAuthenticated) return principal;

        // 서비스 계정(워커·라인)은 인증 핸들러가 역할까지 정해 준다.
        if (identity.AuthenticationType is WorkerTokenAuthenticationHandler.SchemeName
            or LineTokenAuthenticationHandler.SchemeName)
            return principal;

        // 우리가 발급한 토큰(개발 토큰·서비스 토큰)은 실린 역할이 곧 의도다.
        if (principal.HasClaim(ServiceTokenIssuer.SelfIssuedClaim, "1")) return principal;

        var username = principal.FindFirstValue(ClaimTypes.Name)
                       ?? principal.FindFirstValue("unique_name")
                       ?? principal.FindFirstValue(ClaimTypes.NameIdentifier)
                       ?? principal.FindFirstValue("sub");
        if (string.IsNullOrWhiteSpace(username)) return principal;

        var normalized = Data.Entities.Member.Normalize(username);
        var snapshot = await cache.GetOrCreateAsync(Key(normalized), async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = CacheFor;
            using var scope = scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<MlopsDbContext>();
            return await db.Members.AsNoTracking()
                .Where(m => m.Username == normalized)
                .Select(m => new MemberSnapshot(m.Role, m.Disabled, m.MustChangePassword, m.SecurityStamp))
                .FirstOrDefaultAsync();
        });

        // 비활성 계정은 역할을 주지 않는다 — 토큰이 살아 있어도 그 다음 요청부터 모든 정책이 막힌다.
        // 인증 단계가 아니라 여기서 하는 이유는, 이 자리가 요청마다 돌고 캐시를 즉시 버릴 수 있어서다.
        if (snapshot is { Disabled: true }) return WithRole(principal, null);

        // 임시 비밀번호 상태도 같다. 비밀번호 변경만 열려 있어야 하는데 그 엔드포인트는
        // 역할을 요구하지 않는다(인증만). 나머지는 최소 Viewer 를 요구하므로 여기서 다 막힌다.
        if (snapshot is { MustChangePassword: true }) return WithRole(principal, null);

        // 우리가 발급한 로그인 토큰이라면, 비밀번호가 바뀐 뒤의 옛 토큰인지 본다.
        // 운영 웹이 토큰 세대(tv)로 하는 일을 로컬 모드에서는 이 도장으로 한다.
        var stamp = principal.FindFirstValue(LocalTokenIssuer.PasswordStampClaim);
        if (stamp is not null && snapshot is not null
            && !string.Equals(stamp, LocalTokenIssuer.Stamp(snapshot.SecurityStamp), StringComparison.Ordinal))
            return WithRole(principal, null);

        var role = snapshot?.Role;
        if (string.IsNullOrWhiteSpace(role))
        {
            var bootstrap = auth.Value.BootstrapWebRole;
            role = !string.IsNullOrWhiteSpace(bootstrap) && principal.IsInRole(bootstrap)
                ? Roles.Admin
                : auth.Value.DefaultRole;
        }

        return WithRole(principal, role);
    }

    /// <summary>
    /// 토큰이 들고 온 역할을 지우고 표의 역할 하나만 남긴다. 남겨 두면 이름이 겹치는 역할(Admin)이
    /// 표의 결정을 덮어써 강등이 듣지 않는다. <paramref name="role"/> 이 비면 역할 없이 돌려준다 —
    /// 인증은 됐지만 아무 정책도 통과하지 못하는 상태(비활성·임시 비밀번호·끊긴 토큰)다.
    /// </summary>
    private static ClaimsPrincipal WithRole(ClaimsPrincipal principal, string? role)
    {
        var result = principal.Clone();
        var target = (ClaimsIdentity)result.Identity!;
        foreach (var stale in target.FindAll(target.RoleClaimType).ToArray()) target.RemoveClaim(stale);
        foreach (var stale in target.FindAll(ClaimTypes.Role).ToArray()) target.RemoveClaim(stale);
        foreach (var stale in target.FindAll("role").ToArray()) target.RemoveClaim(stale);

        if (!string.IsNullOrWhiteSpace(role)) target.AddClaim(new Claim(ClaimTypes.Role, role));
        return result;
    }

    /// <summary>캐시에 담는 표 한 줄. 역할만 담던 것을 넓혔다 — 비활성과 비밀번호 도장도 매 요청 봐야 한다.</summary>
    private sealed record MemberSnapshot(string Role, bool Disabled, bool MustChangePassword, string? SecurityStamp);
}
