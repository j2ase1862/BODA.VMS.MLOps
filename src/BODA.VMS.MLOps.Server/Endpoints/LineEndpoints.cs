using System.Security.Claims;
using BODA.VMS.MLOps.Contracts;
using BODA.VMS.MLOps.Contracts.Lines;
using BODA.VMS.MLOps.Server.Auth;
using BODA.VMS.MLOps.Server.Services;

namespace BODA.VMS.MLOps.Server.Endpoints;

/// <summary>
/// 라인 PC 서비스 계정 관리 (개발 문서 §5.1·§5.2).
/// 발급·재발급·비활성은 관리자만 한다 — 이 토큰 하나로 라인이 모델을 받아 가기 때문이다.
/// </summary>
public static class LineEndpoints
{
    public static RouteGroupBuilder MapLineEndpoints(this RouteGroupBuilder api)
    {
        var g = api.MapGroup("/line-clients").WithTags("LineClients");

        g.MapGet("/", async (LineClientService svc, CancellationToken ct) =>
            Results.Ok(await svc.ListAsync(ct))).RequireAuthorization(Policies.Engineer);

        g.MapPost("/", async (CreateLineClientRequest req, LineClientService svc, ClaimsPrincipal p, CancellationToken ct) =>
        {
            var r = await svc.CreateAsync(req, CurrentUser.From(p), ct);
            // 토큰 원문은 이 응답에서 1회만 노출된다
            return Results.Created($"/api/line-clients/{r.Line.Id}", r);
        }).RequireAuthorization(Policies.Admin);

        g.MapPost("/{id:guid}/rotate-token", async (Guid id, LineClientService svc, ClaimsPrincipal p, CancellationToken ct) =>
            Results.Ok(await svc.RotateTokenAsync(id, CurrentUser.From(p), ct))).RequireAuthorization(Policies.Admin);

        g.MapPost("/{id:guid}/disable", async (Guid id, DisableLineClientRequest? req, LineClientService svc,
            ClaimsPrincipal p, CancellationToken ct) =>
            Results.Ok(await svc.SetDisabledAsync(id, true, req?.Reason, CurrentUser.From(p), ct)))
            .RequireAuthorization(Policies.Admin);

        g.MapPost("/{id:guid}/enable", async (Guid id, LineClientService svc, ClaimsPrincipal p, CancellationToken ct) =>
            Results.Ok(await svc.SetDisabledAsync(id, false, null, CurrentUser.From(p), ct)))
            .RequireAuthorization(Policies.Admin);

        g.MapDelete("/{id:guid}", async (Guid id, LineClientService svc, ClaimsPrincipal p, CancellationToken ct) =>
        {
            await svc.DeleteAsync(id, CurrentUser.From(p), ct);
            return Results.NoContent();
        }).RequireAuthorization(Policies.Admin);

        // 라인 PC 가 자기 계정이 살아 있는지 확인하는 용도. 토큰이 맞으면 자기 정보를 돌려준다.
        api.MapGet("/line-clients/me", (ClaimsPrincipal p) =>
        {
            var user = CurrentUser.From(p);
            return Results.Ok(new { name = user.Name, lineId = user.LineId, roles = user.RolesSet.ToArray() });
        }).RequireAuthorization(Policies.Line).WithTags("LineClients");

        return api;
    }
}
