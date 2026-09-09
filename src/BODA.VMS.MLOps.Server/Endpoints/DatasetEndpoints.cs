using System.Security.Claims;
using BODA.VMS.MLOps.Contracts;
using BODA.VMS.MLOps.Contracts.Datasets;
using BODA.VMS.MLOps.Core.Domain;
using BODA.VMS.MLOps.Server.Auth;
using BODA.VMS.MLOps.Server.Services;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;

namespace BODA.VMS.MLOps.Server.Endpoints;

/// <summary>
/// 데이터셋과 그 버전 (개발 문서 §5.2).
/// 데이터셋은 라벨링 대상 묶음이고, 버전은 학습이 대상으로 삼는 불변 스냅샷이다.
/// </summary>
public static class DatasetEndpoints
{
    public static RouteGroupBuilder MapDatasetEndpoints(this RouteGroupBuilder api)
    {
        MapDatasets(api);
        MapVersions(api);
        return api;
    }

    private static void MapDatasets(RouteGroupBuilder api)
    {
        var g = api.MapGroup("/datasets").WithTags("Datasets");

        g.MapGet("/", async (string? taskType, bool? archived, DatasetService svc, CancellationToken ct) =>
        {
            var datasets = await svc.ListAsync(EnumBinding.ParseOptional<TaskType>(taskType, "taskType"), archived ?? false, ct);
            var result = new List<DatasetDto>();
            foreach (var d in datasets) result.Add(d.ToDto(await svc.StatsAsync(d.Id, ct)));
            return Results.Ok(result);
        }).RequireAuthorization(Policies.Viewer);

        g.MapPost("/", async (CreateDatasetRequest req, DatasetService svc, ClaimsPrincipal p, CancellationToken ct) =>
        {
            var d = await svc.CreateAsync(req.Name, req.TaskType, req.Classes, req.Description, CurrentUser.From(p), ct);
            return Results.Created($"/api/datasets/{d.Id}", d.ToDto());
        }).RequireAuthorization(Policies.Engineer);

        g.MapGet("/{id:guid}", async (Guid id, DatasetService svc, CancellationToken ct) =>
            Results.Ok((await svc.GetAsync(id, ct)).ToDto(await svc.StatsAsync(id, ct)))).RequireAuthorization(Policies.Viewer);

        g.MapPatch("/{id:guid}", async (Guid id, UpdateDatasetRequest req, DatasetService svc, ClaimsPrincipal p, CancellationToken ct) =>
            Results.Ok((await svc.UpdateAsync(id, req.Name, req.Description, req.IsArchived, CurrentUser.From(p), ct)).ToDto()))
            .RequireAuthorization(Policies.Engineer);

        g.MapDelete("/{id:guid}", async (Guid id, DatasetService svc, ClaimsPrincipal p, CancellationToken ct) =>
        {
            await svc.DeleteAsync(id, CurrentUser.From(p), ct);
            return Results.NoContent();
        }).RequireAuthorization(Policies.Engineer);

        // ── 구성 ──
        g.MapPost("/{id:guid}/images", async (Guid id, AddImagesRequest req, DatasetService svc, ClaimsPrincipal p, CancellationToken ct) =>
            Results.Ok(new { added = await svc.AddImagesAsync(id, req.ImageIds, req.Split, CurrentUser.From(p), ct) }))
            .RequireAuthorization(Policies.Labeler);

        g.MapPost("/{id:guid}/images/remove", async (Guid id, DeleteImagesRequest req, DatasetService svc, ClaimsPrincipal p, CancellationToken ct) =>
            Results.Ok(new { removed = await svc.RemoveImagesAsync(id, req.ImageIds, CurrentUser.From(p), ct) }))
            .RequireAuthorization(Policies.Engineer);

        g.MapPost("/{id:guid}/split", async (Guid id, SetSplitRequest req, DatasetService svc, ClaimsPrincipal p, CancellationToken ct) =>
            Results.Ok(new { changed = await svc.SetSplitAsync(id, req.ImageIds, req.Split, CurrentUser.From(p), ct) }))
            .RequireAuthorization(Policies.Labeler);

        g.MapPost("/{id:guid}/auto-split", async (Guid id, AutoSplitRequest req, DatasetService svc, ClaimsPrincipal p, CancellationToken ct) =>
            Results.Ok(await svc.AutoSplitAsync(id, req.TrainRatio, req.ValRatio, CurrentUser.From(p), ct)))
            .RequireAuthorization(Policies.Engineer);

        // ── 라벨링 ──
        g.MapGet("/{id:guid}/images/{imageId:guid}/labels", async (Guid id, Guid imageId,
            LabelingService svc, ClaimsPrincipal p, CancellationToken ct) =>
            Results.Ok((await svc.GetAsync(id, imageId, CurrentUser.From(p), ct)).ToDto()))
            .RequireAuthorization(Policies.Viewer);

        g.MapPut("/{id:guid}/images/{imageId:guid}/labels", async (Guid id, Guid imageId, SaveLabelsRequest req,
            LabelingService svc, ClaimsPrincipal p, CancellationToken ct) =>
        {
            var annotations = (req.Annotations ?? []).Select(a => a.ToDomain()).ToList();
            return Results.Ok((await svc.SaveAsync(id, imageId, annotations, req.MarkLabeled, CurrentUser.From(p), ct)).ToDto());
        }).RequireAuthorization(Policies.Labeler);

        g.MapPost("/{id:guid}/images/{imageId:guid}/lock", async (Guid id, Guid imageId,
            LabelingService svc, ClaimsPrincipal p, CancellationToken ct) =>
            Results.Ok((await svc.LockAsync(id, imageId, CurrentUser.From(p), ct)).ToDto()))
            .RequireAuthorization(Policies.Labeler);

        g.MapPost("/{id:guid}/images/{imageId:guid}/unlock", async (Guid id, Guid imageId,
            LabelingService svc, ClaimsPrincipal p, CancellationToken ct) =>
        {
            await svc.UnlockAsync(id, imageId, CurrentUser.From(p), ct);
            return Results.NoContent();
        }).RequireAuthorization(Policies.Labeler);

        g.MapPost("/{id:guid}/images/{imageId:guid}/review", async (Guid id, Guid imageId, ReviewRequest req,
            LabelingService svc, ClaimsPrincipal p, CancellationToken ct) =>
            Results.Ok((await svc.ReviewAsync(id, imageId, req.Approved, CurrentUser.From(p), ct)).ToDto()))
            .RequireAuthorization(Policies.Engineer);

        g.MapGet("/{id:guid}/next-to-label", async (Guid id, Guid? after,
            LabelingService svc, ClaimsPrincipal p, CancellationToken ct) =>
            Results.Ok(new NextImageDto(await svc.NextToLabelAsync(id, after, CurrentUser.From(p), ct))))
            .RequireAuthorization(Policies.Labeler);

        // 후보 모델의 추론 결과를 초기 라벨로 채운다 (개발 문서 §5.4 Active Learning).
        // 부르는 쪽은 보통 워커의 사전 라벨링 작업이다. 사람이 이미 손댄 이미지는 건드리지 않는다.
        g.MapPost("/{id:guid}/prefill", async (Guid id, PrefillRequest req,
            LabelingService svc, IOptions<MlopsOptions> options, ClaimsPrincipal p, CancellationToken ct) =>
        {
            if (req.Images is null or { Count: 0 })
                throw ApiException.BadRequest(ErrorCodes.Validation, "채울 이미지가 없습니다.");
            if (req.Images.Count > options.Value.MaxPrefillImages)
                throw ApiException.BadRequest(ErrorCodes.Validation,
                    $"한 번에 {options.Value.MaxPrefillImages}장까지 보낼 수 있습니다. 나눠서 보내세요.");

            var predictions = req.Images.ToDictionary(
                i => i.ImageId,
                i => (IReadOnlyList<Core.Labeling.LabelAnnotation>)
                    (i.Annotations ?? []).Select(a => a.ToDomain()).ToList());
            var uncertainty = req.Images
                .Where(i => i.Uncertainty is not null)
                .ToDictionary(i => i.ImageId, i => i.Uncertainty!.Value);

            int filled = await svc.PrefillAsync(id, predictions, uncertainty, CurrentUser.From(p), ct);
            return Results.Ok(new PrefillResultDto(filled, req.Images.Count));
        }).RequireAuthorization(Policies.WorkerOrEngineer);

        // 후보 모델을 돌려 초기 라벨과 불확실도를 채운다 (§5.4 Active Learning).
        // 추론이 도는 동안 응답을 붙잡고 있으므로 MaxImages 로 나눠 부른다.
        g.MapPost("/{id:guid}/prelabel", async (Guid id, PrelabelRequest req,
            PrelabelService svc, ClaimsPrincipal p, CancellationToken ct) =>
        {
            var result = await svc.RunAsync(id, req.ModelVersionId,
                Math.Clamp(req.Confidence, 0.01, 0.99),
                req.MaxImages, CurrentUser.From(p), ct);
            return Results.Ok(new PrelabelResultDto(result.Considered, result.Inferred, result.Filled,
                result.Skipped, result.Annotations, result.Message));
        }).RequireAuthorization(Policies.Engineer);

        // ── 스냅샷 ──
        g.MapPost("/{id:guid}/versions", async (Guid id, CreateSnapshotRequest? req,
            DatasetSnapshotService svc, ClaimsPrincipal p, CancellationToken ct) =>
        {
            req ??= new CreateSnapshotRequest();
            var v = await svc.CreateSnapshotAsync(id, req.Name, req.IncludeUnlabeled, req.ReviewedOnly, CurrentUser.From(p), ct);
            return Results.Created($"/api/dataset-versions/{v.Id}", v.ToDto());
        }).RequireAuthorization(Policies.Engineer);
    }

    private static void MapVersions(RouteGroupBuilder api)
    {
        var g = api.MapGroup("/dataset-versions").WithTags("Datasets");

        g.MapGet("/", async (Guid? datasetId, DatasetVersionService svc, CancellationToken ct) =>
            Results.Ok(await svc.ListAsync(datasetId, ct))).RequireAuthorization(Policies.Viewer);

        // 밖에서 만든 내보내기 zip 을 그대로 버전으로 등록하는 길 (WPF 도구 등)
        g.MapPost("/", async (HttpRequest request, DatasetVersionService svc, ClaimsPrincipal p, CancellationToken ct) =>
        {
            var (file, meta) = await ModelEndpoints.ReadUpload<DatasetVersionUploadMeta>(request, ct);
            if (meta is null) throw ApiException.BadRequest(ErrorCodes.Validation, "'meta' 파트(name, taskType, exportFormat)가 필요합니다.");
            await using var s = file.OpenReadStream();
            var dto = await svc.UploadAsync(meta, s, CurrentUser.From(p), ct);
            return Results.Created($"/api/dataset-versions/{dto.Id}", dto);
        }).RequireAuthorization(Policies.Engineer);

        g.MapGet("/{id:guid}", async (Guid id, DatasetVersionService svc, CancellationToken ct) =>
            Results.Ok(await svc.GetAsync(id, ct))).RequireAuthorization(Policies.Viewer);

        g.MapDelete("/{id:guid}", async (Guid id, DatasetVersionService svc, ClaimsPrincipal p, CancellationToken ct) =>
        {
            await svc.DeleteAsync(id, CurrentUser.From(p), ct);
            return Results.NoContent();
        }).RequireAuthorization(Policies.Engineer);

        // 워커가 학습 직전에 받는 곳. 스냅샷은 여기서 처음 요청될 때 zip 이 만들어진다.
        g.MapGet("/{id:guid}/export", async (Guid id, string? format, HttpResponse response,
            DatasetSnapshotService snapshots, CancellationToken ct) =>
        {
            var (stream, dv) = await snapshots.OpenExportAsync(id, format, ct);
            response.Headers["X-Content-Sha256"] = dv.ManifestHash;
            return Results.File(stream, "application/zip",
                $"{Core.Export.DatasetExportWriter.SafeSegment(dv.Name)}-{dv.ManifestHash[..12]}.zip",
                enableRangeProcessing: true, entityTag: new EntityTagHeaderValue($"\"{dv.ManifestHash}\""));
        }).RequireAuthorization(Policies.WorkerOrEngineer);
    }
}
