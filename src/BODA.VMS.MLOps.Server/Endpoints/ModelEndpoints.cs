using System.Security.Claims;
using System.Text.Json;
using BODA.VMS.MLOps.Contracts;
using BODA.VMS.MLOps.Contracts.Models;
using BODA.VMS.MLOps.Core.Domain;
using BODA.VMS.MLOps.Server.Auth;
using BODA.VMS.MLOps.Server.Services;
using Microsoft.Net.Http.Headers;

namespace BODA.VMS.MLOps.Server.Endpoints;

/// <summary>모델 레지스트리 API (Phase 1 §5)</summary>
public static class ModelEndpoints
{
    public static RouteGroupBuilder MapModelEndpoints(this RouteGroupBuilder api)
    {
        var models = api.MapGroup("/models").WithTags("Models");

        models.MapGet("/", async (string? taskType, bool? archived, ModelRegistryService svc, CancellationToken ct) =>
            Results.Ok(await svc.ListModelsAsync(EnumBinding.ParseOptional<TaskType>(taskType, "taskType"), archived ?? false, ct)))
            .RequireAuthorization(Policies.Viewer);

        models.MapPost("/", async (CreateModelRequest req, ModelRegistryService svc, ClaimsPrincipal p, CancellationToken ct) =>
        {
            var dto = await svc.CreateModelAsync(req, CurrentUser.From(p), ct);
            return Results.Created($"/api/models/{dto.Id}", dto);
        }).RequireAuthorization(Policies.Engineer);

        models.MapGet("/{id:guid}", async (Guid id, ModelRegistryService svc, CancellationToken ct) =>
            Results.Ok(await svc.GetModelAsync(id, ct))).RequireAuthorization(Policies.Viewer);

        models.MapPatch("/{id:guid}", async (Guid id, UpdateModelRequest req, ModelRegistryService svc, ClaimsPrincipal p, CancellationToken ct) =>
            Results.Ok(await svc.UpdateModelAsync(id, req, CurrentUser.From(p), ct))).RequireAuthorization(Policies.Engineer);

        models.MapGet("/{id:guid}/versions", async (Guid id, ModelRegistryService svc, CancellationToken ct) =>
            Results.Ok(await svc.ListVersionsAsync(id, ct))).RequireAuthorization(Policies.Viewer);

        // multipart: file(.onnx) + meta(json: classes?, inputSize?, license?, notes?, metrics?)
        models.MapPost("/{id:guid}/versions", async (Guid id, HttpRequest request, ModelRegistryService svc, ClaimsPrincipal p, CancellationToken ct) =>
        {
            var (file, meta) = await ReadUpload<VersionUploadMeta>(request, ct);
            await using var stream = file.OpenReadStream();
            var (dto, created) = await svc.UploadVersionAsync(id, stream, meta, VersionSource.Upload, null, CurrentUser.From(p), ct);
            return created ? Results.Created($"/api/model-versions/{dto.Id}", dto) : Results.Ok(dto);
        }).RequireAuthorization(Policies.WorkerOrEngineer);

        models.MapGet("/{id:guid}/resolve", async (Guid id, string? stage, int? version, ModelRegistryService svc, ClaimsPrincipal p, CancellationToken ct) =>
            Results.Ok(await svc.ResolveAsync(id, stage, version, ct, CurrentUser.From(p)))).RequireAuthorization(Policies.Line);

        // 라인 배포 이력 — 어느 라인이 어느 버전을 언제 받아 갔는지 (Engineer 이상)
        models.MapGet("/{id:guid}/deliveries", async (Guid id, Guid? versionId, int? take, ModelRegistryService svc, CancellationToken ct) =>
            Results.Ok(await svc.ListDeliveriesAsync(id, versionId, take ?? 100, ct))).RequireAuthorization(Policies.Engineer);

        var versions = api.MapGroup("/model-versions").WithTags("Models");

        versions.MapGet("/{id:guid}", async (Guid id, ModelRegistryService svc, CancellationToken ct) =>
            Results.Ok(await svc.GetVersionAsync(id, ct))).RequireAuthorization(Policies.Viewer);

        // ETag = "sha256" · If-None-Match → 304 · Range 지원
        versions.MapGet("/{id:guid}/artifact", async (Guid id, HttpRequest request, ModelRegistryService svc, ClaimsPrincipal p, CancellationToken ct) =>
        {
            // Range 로 이어받는 조각까지 다 남기면 "몇 번 받아 갔나" 가 뜻을 잃는다 — 첫 조각만 센다
            var range = request.Headers.Range.ToString();
            bool firstChunk = string.IsNullOrEmpty(range) || range.Contains("=0-", StringComparison.Ordinal);
            var (stream, v) = await svc.OpenArtifactAsync(id, ct, CurrentUser.From(p), firstChunk);
            return Results.File(stream, "application/octet-stream", $"{v.Sha256}.onnx", enableRangeProcessing: true,
                entityTag: new EntityTagHeaderValue($"\"{v.Sha256}\""));
        }).RequireAuthorization(Policies.Line);

        versions.MapPost("/{id:guid}/promote", async (Guid id, PromoteRequest req, ModelRegistryService svc, ClaimsPrincipal p, CancellationToken ct) =>
            Results.Ok(await svc.PromoteAsync(id, req, CurrentUser.From(p), ct))).RequireAuthorization(Policies.Engineer);

        return api;
    }

    /// <summary>multipart 읽기 — 'file' 파트(없으면 첫 파일) + 'meta' JSON 파트</summary>
    public static async Task<(IFormFile File, TMeta? Meta)> ReadUpload<TMeta>(HttpRequest request, CancellationToken ct) where TMeta : class
    {
        if (!request.HasFormContentType)
            throw ApiException.BadRequest(ErrorCodes.Validation, "multipart/form-data 요청이어야 합니다.");
        var form = await request.ReadFormAsync(ct);
        var file = form.Files.GetFile("file") ?? form.Files.FirstOrDefault()
                   ?? throw ApiException.BadRequest(ErrorCodes.Validation, "'file' 파트가 필요합니다.");
        TMeta? meta = null;
        if (form.TryGetValue("meta", out var raw) && !string.IsNullOrWhiteSpace(raw))
        {
            try { meta = JsonSerializer.Deserialize<TMeta>(raw!, MlopsJson.Options); }
            catch (JsonException ex) { throw ApiException.BadRequest(ErrorCodes.Validation, $"meta JSON 파싱 실패: {ex.Message}"); }
        }
        return (file, meta);
    }
}
