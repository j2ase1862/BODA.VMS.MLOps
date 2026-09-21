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

        return api;
    }
}
