using System.Security.Claims;
using BODA.VMS.MLOps.Contracts.Models;
using BODA.VMS.MLOps.Server.Auth;
using BODA.VMS.MLOps.Server.Services;

namespace BODA.VMS.MLOps.Server.Endpoints;

/// <summary>레시피 바인딩 API (Phase 1 §5, §6.4)</summary>
public static class BindingEndpoints
{
    public static RouteGroupBuilder MapBindingEndpoints(this RouteGroupBuilder api)
    {
        var recipes = api.MapGroup("/recipes").WithTags("Bindings");

        recipes.MapPut("/{recipeId}/tools/{toolId}/model",
            async (string recipeId, string toolId, SetBindingRequest req, BindingService svc, ClaimsPrincipal p, CancellationToken ct) =>
                Results.Ok(await svc.SetAsync(recipeId, toolId, req, CurrentUser.From(p), ct)))
            .RequireAuthorization(Policies.Engineer);

        recipes.MapGet("/{recipeId}/model-bindings", async (string recipeId, BindingService svc, CancellationToken ct) =>
            Results.Ok(await svc.ListAsync(recipeId, ct))).RequireAuthorization(Policies.Viewer);

        // 레시피 동기화 페이로드 확장 — VMS 프리페치용
        recipes.MapGet("/{recipeId}/model-bindings/payload", async (string recipeId, BindingService svc, CancellationToken ct) =>
            Results.Ok(await svc.PayloadAsync(recipeId, ct))).RequireAuthorization(Policies.Line);

        var bindings = api.MapGroup("/model-bindings").WithTags("Bindings");

        bindings.MapGet("/{id:guid}", async (Guid id, BindingService svc, CancellationToken ct) =>
            Results.Ok(await svc.GetAsync(id, ct))).RequireAuthorization(Policies.Viewer);

        bindings.MapPost("/{id:guid}/rollback", async (Guid id, RollbackRequest? req, BindingService svc, ClaimsPrincipal p, CancellationToken ct) =>
            Results.Ok(await svc.RollbackAsync(id, req ?? new RollbackRequest(), CurrentUser.From(p), ct)))
            .RequireAuthorization(Policies.Engineer);

        return api;
    }
}
