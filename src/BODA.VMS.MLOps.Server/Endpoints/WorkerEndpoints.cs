using System.Security.Claims;
using BODA.VMS.MLOps.Contracts;
using BODA.VMS.MLOps.Contracts.Workers;
using BODA.VMS.MLOps.Server.Auth;
using BODA.VMS.MLOps.Server.Services;

namespace BODA.VMS.MLOps.Server.Endpoints;

/// <summary>워커 관리(Admin) + 워커 프로토콜(register/heartbeat/scripts) (Phase 3 §4)</summary>
public static class WorkerEndpoints
{
    public sealed record DisableRequest(string? Reason = null);

    public static RouteGroupBuilder MapWorkerEndpoints(this RouteGroupBuilder api)
    {
        var g = api.MapGroup("/workers").WithTags("Workers");

        // ── 관리 ──
        g.MapGet("/", async (WorkerRegistryService svc, CancellationToken ct) => Results.Ok(await svc.ListAsync(ct)))
            .RequireAuthorization(Policies.Viewer);

        g.MapPost("/", async (CreateWorkerRequest req, WorkerRegistryService svc, ClaimsPrincipal p, CancellationToken ct) =>
        {
            var r = await svc.CreateAsync(req, CurrentUser.From(p), ct);
            return Results.Created($"/api/workers/{r.WorkerId}", r); // 토큰은 이 응답에서 1회만 노출
        }).RequireAuthorization(Policies.Admin);

        g.MapGet("/{id:guid}", async (Guid id, WorkerRegistryService svc, CancellationToken ct) => Results.Ok(await svc.GetAsync(id, ct)))
            .RequireAuthorization(Policies.Viewer);

        g.MapPost("/{id:guid}/rotate-token", async (Guid id, WorkerRegistryService svc, ClaimsPrincipal p, CancellationToken ct) =>
            Results.Ok(await svc.RotateTokenAsync(id, CurrentUser.From(p), ct))).RequireAuthorization(Policies.Admin);

        g.MapPost("/{id:guid}/disable", async (Guid id, DisableRequest? req, WorkerRegistryService svc, ClaimsPrincipal p, CancellationToken ct) =>
            Results.Ok(await svc.SetDisabledAsync(id, true, req?.Reason, CurrentUser.From(p), ct))).RequireAuthorization(Policies.Admin);

        g.MapPost("/{id:guid}/enable", async (Guid id, WorkerRegistryService svc, ClaimsPrincipal p, CancellationToken ct) =>
            Results.Ok(await svc.SetDisabledAsync(id, false, null, CurrentUser.From(p), ct))).RequireAuthorization(Policies.Admin);

        // ── 워커 프로토콜 (워커 토큰) ──
        g.MapPost("/register", async (RegisterWorkerRequest req, HttpRequest http, WorkerRegistryService svc, ClaimsPrincipal p, CancellationToken ct) =>
        {
            var workerId = RequireWorkerId(p);
            var protocol = int.TryParse(http.Headers[MlopsJson.ProtocolHeader], out var v) ? v : 0;
            return Results.Ok(await svc.RegisterAsync(workerId, req, protocol, ct));
        }).RequireAuthorization(Policies.Worker);

        g.MapPost("/{id:guid}/heartbeat", async (Guid id, HeartbeatRequest req, WorkerRegistryService svc, ClaimsPrincipal p, CancellationToken ct) =>
        {
            if (RequireWorkerId(p) != id) throw ApiException.Forbidden("토큰의 워커와 경로의 워커가 다릅니다.");
            return Results.Ok(await svc.HeartbeatAsync(id, req, ct));
        }).RequireAuthorization(Policies.Worker);

        g.MapGet("/scripts", (ScriptManifestService scripts) =>
        {
            var (m, at) = scripts.GetManifest();
            return Results.Ok(new ScriptsManifest(m, at));
        }).RequireAuthorization(Policies.WorkerOrEngineer);

        g.MapGet("/scripts/{name}", (string name, ScriptManifestService scripts) =>
        {
            var path = scripts.GetPath(name) ?? throw ApiException.NotFound($"스크립트 {name}");
            return Results.File(path, "text/x-python", name);
        }).RequireAuthorization(Policies.WorkerOrEngineer);

        return api;
    }

    public static Guid RequireWorkerId(ClaimsPrincipal p) =>
        CurrentUser.From(p).WorkerId ?? throw ApiException.Forbidden("워커 토큰이 필요합니다.");
}
