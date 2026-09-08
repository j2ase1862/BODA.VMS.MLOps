using System.Security.Claims;
using BODA.VMS.MLOps.Contracts;
using BODA.VMS.MLOps.Contracts.Pretrained;
using BODA.VMS.MLOps.Server.Auth;
using BODA.VMS.MLOps.Server.Services;
using Microsoft.Net.Http.Headers;

namespace BODA.VMS.MLOps.Server.Endpoints;

/// <summary>사전학습 가중치 미러 API (Phase 3 §4, §9)</summary>
public static class PretrainedEndpoints
{
    public static RouteGroupBuilder MapPretrainedEndpoints(this RouteGroupBuilder api)
    {
        var g = api.MapGroup("/pretrained").WithTags("Pretrained");

        g.MapGet("/", async (PretrainedMirrorService svc, CancellationToken ct) => Results.Ok(await svc.ListAsync(ct)))
            .RequireAuthorization(Policies.Viewer);

        g.MapPost("/", async (CreatePretrainedAssetRequest req, PretrainedMirrorService svc, ClaimsPrincipal p, CancellationToken ct) =>
        {
            var dto = await svc.CreateAsync(req, CurrentUser.From(p), ct);
            return Results.Created($"/api/pretrained/{dto.Ref}", dto);
        }).RequireAuthorization(Policies.Admin);

        g.MapGet("/{ref}", async (string @ref, PretrainedMirrorService svc, CancellationToken ct) =>
            Results.Ok(await svc.GetAsync(PretrainedMirrorService.ValidateRef(@ref), ct))).RequireAuthorization(Policies.Viewer);

        g.MapDelete("/{ref}", async (string @ref, PretrainedMirrorService svc, ClaimsPrincipal p, CancellationToken ct) =>
        {
            await svc.DeleteAsync(PretrainedMirrorService.ValidateRef(@ref), CurrentUser.From(p), ct);
            return Results.NoContent();
        }).RequireAuthorization(Policies.Admin);

        // multipart: 파일 파트 여러 개 허용 (파일명 = 저장 이름)
        g.MapPost("/{ref}/files", async (string @ref, HttpRequest request, PretrainedMirrorService svc, ClaimsPrincipal p, CancellationToken ct) =>
        {
            var r = PretrainedMirrorService.ValidateRef(@ref);
            if (!request.HasFormContentType) throw ApiException.BadRequest(ErrorCodes.Validation, "multipart/form-data 요청이어야 합니다.");
            var form = await request.ReadFormAsync(ct);
            if (form.Files.Count == 0) throw ApiException.BadRequest(ErrorCodes.Validation, "파일 파트가 필요합니다.");
            PretrainedAssetDto? dto = null;
            foreach (var f in form.Files)
            {
                await using var s = f.OpenReadStream();
                dto = await svc.AddFileAsync(r, string.IsNullOrWhiteSpace(f.FileName) ? f.Name : f.FileName, s, CurrentUser.From(p), ct);
            }
            return Results.Ok(dto);
        }).RequireAuthorization(Policies.Admin);

        // 워커가 학습 전에 받아야 하는 파일 — 워커 토큰에 열린 몇 안 되는 읽기 중 하나 (Phase 3 §8)
        g.MapGet("/{ref}/{file}", async (string @ref, string file, PretrainedMirrorService svc, CancellationToken ct) =>
        {
            var (stream, entry) = await svc.OpenFileAsync(PretrainedMirrorService.ValidateRef(@ref), file, ct);
            return Results.File(stream, "application/octet-stream", entry.FileName, enableRangeProcessing: true,
                entityTag: new EntityTagHeaderValue($"\"{entry.Sha256}\""));
        }).RequireAuthorization(Policies.WorkerOrEngineer);

        return api;
    }
}
