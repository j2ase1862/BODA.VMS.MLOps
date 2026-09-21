using System.Security.Claims;
using BODA.VMS.MLOps.Contracts.Members;
using BODA.VMS.MLOps.Server.Auth;
using BODA.VMS.MLOps.Server.Services;

namespace BODA.VMS.MLOps.Server.Endpoints;

/// <summary>
/// 운영 웹 계정에 MLOps 역할을 붙이는 화면의 뒷면 (개발 문서 §6).
/// 역할을 주고 거두는 것은 관리자만 한다 — 이 표가 곧 누가 무엇을 할 수 있는지다.
/// </summary>
public static class MemberEndpoints
{
    public static RouteGroupBuilder MapMemberEndpoints(this RouteGroupBuilder api)
    {
        var g = api.MapGroup("/members").WithTags("Members");

        g.MapGet("/", async (MemberService svc, CancellationToken ct) =>
            Results.Ok(await svc.ListAsync(ct))).RequireAuthorization(Policies.Admin);

        // 표에 없는 사람이 무엇이 되는지는 설정이 정한다. 화면이 그것을 설명해야
        // "왜 이 사람은 목록에 없는데 들어와 있지" 로 헤매지 않는다.
        g.MapGet("/policy", (MemberService svc) => Results.Ok(svc.Policy()))
            .RequireAuthorization(Policies.Admin);

        g.MapPut("/", async (GrantMemberRequest req, MemberService svc, ClaimsPrincipal p, CancellationToken ct) =>
            Results.Ok(await svc.GrantAsync(req, CurrentUser.From(p), ct))).RequireAuthorization(Policies.Admin);

        g.MapDelete("/{id:guid}", async (Guid id, MemberService svc, ClaimsPrincipal p, CancellationToken ct) =>
        {
            await svc.RevokeAsync(id, CurrentUser.From(p), ct);
            return Results.NoContent();
        }).RequireAuthorization(Policies.Admin);

        // 임시 비밀번호를 새로 내준다. 자체 계정 로그인(Auth:Mode=Local)에서만 뜻이 있다 —
        // Web 모드의 비밀번호는 운영 웹 것이라 우리가 건드릴 수 없다.
        // 만들어진 비밀번호는 이 응답에서 한 번만 나가고 서버에는 해시만 남는다.
        g.MapPost("/{id:guid}/reset-password", async (Guid id, LocalAccountService accounts, ClaimsPrincipal p,
            CancellationToken ct) =>
        {
            if (!accounts.Enabled) return Results.NotFound();
            return Results.Ok(await accounts.ResetPasswordAsync(id, CurrentUser.From(p), ct));
        }).RequireAuthorization(Policies.Admin);

        // 비활성은 두 모드 모두에서 듣는다. 운영 웹 계정이라도 여기서 끊을 수 있어야 한다 —
        // 그쪽에서 지우기 전에 이 서버에서만 먼저 막아야 하는 일이 있다.
        g.MapPost("/{id:guid}/disabled", async (Guid id, SetMemberDisabledRequest req, LocalAccountService accounts,
            ClaimsPrincipal p, CancellationToken ct) =>
        {
            await accounts.SetDisabledAsync(id, req.Disabled, CurrentUser.From(p), ct);
            return Results.NoContent();
        }).RequireAuthorization(Policies.Admin);

        return api;
    }
}
