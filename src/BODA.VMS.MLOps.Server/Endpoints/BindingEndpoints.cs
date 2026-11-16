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

        // 운영 웹의 레시피 목록 — 부른 사람의 토큰으로 그쪽 읽기 API 를 당겨 온다 (ML-02). 못 받으면 이유만 돌려준다.
        recipes.MapGet("/", async (HttpRequest request, ClaimsPrincipal p, WebRecipeDirectoryClient web, CancellationToken ct) =>
            Results.Ok(await web.ListAsync(p, request.Headers.Authorization.ToString() is { Length: > 0 } h ? h : null, ct)))
            .RequireAuthorization(Policies.Viewer);

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
