using System.Security.Claims;
using BODA.VMS.MLOps.Contracts;
using BODA.VMS.MLOps.Contracts.Datasets;
using BODA.VMS.MLOps.Core.Domain;
using BODA.VMS.MLOps.Server.Auth;
using BODA.VMS.MLOps.Server.Services;
using Microsoft.Net.Http.Headers;

namespace BODA.VMS.MLOps.Server.Endpoints;

/// <summary>이미지 풀 API (개발 문서 §5.2 수집·큐레이션)</summary>
public static class ImageEndpoints
{
    public static RouteGroupBuilder MapImageEndpoints(this RouteGroupBuilder api)
    {
        var g = api.MapGroup("/images").WithTags("Images");

        g.MapGet("/", async (Guid? datasetId, bool? inDataset, string? source, string? lineId, string? tag,
            string? search, string? labelStatus, int? skip, int? take,
            ImagePoolService pool, DatasetQueryService query, CancellationToken ct) =>
        {
            var q = new ImagePoolService.ImageQuery(
                datasetId, inDataset,
                EnumBinding.ParseOptional<ImageSource>(source, "source"),
                lineId, tag, search,
                EnumBinding.ParseOptional<LabelStatus>(labelStatus, "labelStatus"),
                skip ?? 0, take ?? 60);
            var (items, total) = await pool.ListAsync(q, ct);
            var dtos = await query.DecorateAsync(items, datasetId, ct);
            return Results.Ok(new ImagePageDto(dtos, total, q.Skip, q.Take));
        }).RequireAuthorization(Policies.Viewer);

        // multipart 로 여러 장을 한 번에. 폼 필드로 source·lineId·tags 를 함께 받는다.
        g.MapPost("/", async (HttpRequest request, ImagePoolService pool, ClaimsPrincipal p, CancellationToken ct) =>
        {
            if (!request.HasFormContentType)
                throw ApiException.BadRequest(ErrorCodes.Validation, "multipart/form-data 요청이어야 합니다.");
            var form = await request.ReadFormAsync(ct);
            if (form.Files.Count == 0)
                throw ApiException.BadRequest(ErrorCodes.Validation, "이미지 파일이 없습니다.");

            var user = CurrentUser.From(p);
            var source = EnumBinding.ParseOptional<ImageSource>(form["source"], "source") ?? ImageSource.Manual;
            var lineId = Trim(form["lineId"]);
            var inspectionId = Trim(form["inspectionId"]);
            var tags = (form["tags"].ToString() ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            var results = new List<ImageUploadResultDto>();
            var errors = new List<string>();
            foreach (var file in form.Files)
            {
                try
                {
                    await using var stream = file.OpenReadStream();
                    var r = await pool.UploadAsync(stream, file.FileName, source, lineId, inspectionId, tags, null, user, ct);
                    results.Add(new ImageUploadResultDto(r.Image.ToDto(), r.Created, r.NearDuplicateOf));
                }
                catch (ApiException ex)
                {
                    // 한 장이 잘못돼도 나머지는 올린다
                    errors.Add($"{file.FileName}: {ex.Message}");
                }
            }
            return Results.Ok(new ImageUploadBatchDto(results, results.Count(r => r.Created),
                results.Count(r => !r.Created), errors.ToArray()));
        }).RequireAuthorization(Policies.Labeler);

        g.MapGet("/{id:guid}", async (Guid id, ImagePoolService pool, CancellationToken ct) =>
            Results.Ok((await pool.GetAsync(id, ct)).ToDto())).RequireAuthorization(Policies.Viewer);

        // 내용 주소 지정이라 파일이 바뀌지 않는다 — 브라우저가 오래 캐시해도 안전하다
        g.MapGet("/{id:guid}/{variant}", async (Guid id, string variant, HttpResponse response,
            ImagePoolService pool, CancellationToken ct) =>
        {
            if (variant is not ("thumb" or "thumbnail" or "view" or "original"))
                throw ApiException.BadRequest(ErrorCodes.Validation, "variant: thumb|view|original");
            var (stream, contentType, fileName, etag) = await pool.OpenAsync(id, variant, ct);
            response.Headers.CacheControl = "private, max-age=604800, immutable";
            return Results.File(stream, contentType, fileName, enableRangeProcessing: true,
                entityTag: new EntityTagHeaderValue(etag));
        }).RequireAuthorization(Policies.Viewer);

        g.MapPost("/tags", async (TagImagesRequest req, ImagePoolService pool, ClaimsPrincipal p, CancellationToken ct) =>
        {
            await pool.SetTagsAsync(req.ImageIds, req.Add ?? [], req.Remove ?? [], CurrentUser.From(p), ct);
            return Results.NoContent();
        }).RequireAuthorization(Policies.Labeler);

        g.MapPost("/delete", async (DeleteImagesRequest req, ImagePoolService pool, ClaimsPrincipal p, CancellationToken ct) =>
            Results.Ok(new { deleted = await pool.DeleteAsync(req.ImageIds, CurrentUser.From(p), ct) }))
            .RequireAuthorization(Policies.Engineer);

        g.MapGet("/duplicates", async (int? take, ImagePoolService pool, DatasetQueryService query, CancellationToken ct) =>
        {
            var groups = await pool.FindDuplicateGroupsAsync(take ?? 1000, ct);
            var result = new List<DuplicateGroupDto>();
            foreach (var group in groups)
                result.Add(new DuplicateGroupDto(await query.ByIdsAsync(group, ct)));
            return Results.Ok(result);
        }).RequireAuthorization(Policies.Viewer);

        return api;
    }

    private static string? Trim(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
