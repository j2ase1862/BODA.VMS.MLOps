using System.Security.Claims;
using BODA.VMS.MLOps.Contracts;
using BODA.VMS.MLOps.Contracts.Datasets;
using BODA.VMS.MLOps.Server.Auth;
using BODA.VMS.MLOps.Server.Services;
using Microsoft.Net.Http.Headers;

namespace BODA.VMS.MLOps.Server.Endpoints;

/// <summary>데이터셋 버전(Phase 2 선행 최소 구현) — 워커 export 규약 (Phase 3 §4)</summary>
public static class DatasetEndpoints
{
    public static RouteGroupBuilder MapDatasetEndpoints(this RouteGroupBuilder api)
    {
        var g = api.MapGroup("/dataset-versions").WithTags("Datasets");

        g.MapGet("/", async (DatasetVersionService svc, CancellationToken ct) => Results.Ok(await svc.ListAsync(ct)))
            .RequireAuthorization(Policies.Viewer);

        // multipart: file(zip) + meta(json: name, taskType, exportFormat, imageCount?, classes?)
        g.MapPost("/", async (HttpRequest request, DatasetVersionService svc, ClaimsPrincipal p, CancellationToken ct) =>
        {
            var (file, meta) = await ModelEndpoints.ReadUpload<DatasetVersionUploadMeta>(request, ct);
            if (meta is null) throw ApiException.BadRequest(ErrorCodes.Validation, "'meta' 파트(name, taskType, exportFormat)가 필요합니다.");
            await using var s = file.OpenReadStream();
            var dto = await svc.UploadAsync(meta, s, CurrentUser.From(p), ct);
            return Results.Created($"/api/dataset-versions/{dto.Id}", dto);
        }).RequireAuthorization(Policies.Engineer);

        g.MapGet("/{id:guid}", async (Guid id, DatasetVersionService svc, CancellationToken ct) => Results.Ok(await svc.GetAsync(id, ct)))
            .RequireAuthorization(Policies.Viewer);

        // ETag = manifestHash
        g.MapGet("/{id:guid}/export", async (Guid id, string? format, HttpResponse response, DatasetVersionService svc, CancellationToken ct) =>
        {
            var (stream, dv) = await svc.OpenExportAsync(id, format, ct);
            // 워커가 zip 무결성을 검증할 수 있도록 파일 해시를 별도 헤더로도 준다 (Phase 2 에서 manifestHash ≠ zip 해시가 되어도 유효)
            response.Headers["X-Content-Sha256"] = dv.ManifestHash;
            return Results.File(stream, "application/zip", $"{dv.Name}-{dv.ManifestHash[..12]}.zip", enableRangeProcessing: true,
                entityTag: new EntityTagHeaderValue($"\"{dv.ManifestHash}\""));
        }).RequireAuthorization(Policies.WorkerOrEngineer);

        return api;
    }
}
