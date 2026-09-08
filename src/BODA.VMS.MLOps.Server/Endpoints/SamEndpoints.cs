using BODA.VMS.MLOps.Contracts;
using BODA.VMS.MLOps.Contracts.Datasets;
using BODA.VMS.MLOps.Core.Sam;
using BODA.VMS.MLOps.Server.Auth;
using BODA.VMS.MLOps.Server.Services;
using BODA.VMS.MLOps.Server.Services.Sam;
using Microsoft.Extensions.Options;

namespace BODA.VMS.MLOps.Server.Endpoints;

/// <summary>SAM 보조 라벨링 API (개발 문서 §5.4) — 클릭 → 폴리곤</summary>
public static class SamEndpoints
{
    public static RouteGroupBuilder MapSamEndpoints(this RouteGroupBuilder api)
    {
        var g = api.MapGroup("/sam").WithTags("SAM");

        // 화면이 SAM 버튼을 보일지 정하는 데 쓴다. 모델이 없어도 오류가 아니라 "없음"이라고 답한다.
        g.MapGet("/status", (SamAssistService sam) =>
        {
            var (available, ready, message) = sam.Status();
            return Results.Ok(new SamStatusDto(available, ready, message));
        }).RequireAuthorization(Policies.Viewer);

        // 이미지를 열 때 미리 부른다 — 인코더가 무거워서 첫 클릭을 여기서 먼저 치워 둔다
        g.MapPost("/prepare", async (SamPrepareRequest req, SamAssistService sam, ImagePoolService pool, CancellationToken ct) =>
        {
            if (!sam.Available) return Results.NoContent();
            var image = await pool.GetAsync(req.ImageId, ct);
            await sam.PrepareAsync(image.Sha256, token => OpenAsync(pool, req.ImageId, token), ct);
            return Results.NoContent();
        }).RequireAuthorization(Policies.Labeler);

        g.MapPost("/predict", async (SamPredictRequest req, SamAssistService sam, ImagePoolService pool,
            IOptions<SamOptions> options, CancellationToken ct) =>
        {
            var status = sam.Status();
            if (!status.Available)
                throw ApiException.BadRequest(ErrorCodes.Validation, status.Message ?? "SAM 보조를 쓸 수 없습니다.");

            if (req.Points is null or { Count: 0 })
                throw ApiException.BadRequest(ErrorCodes.Validation, "클릭한 점이 없습니다.");
            if (req.Points.Count > options.Value.MaxPoints)
                throw ApiException.BadRequest(ErrorCodes.Validation, $"클릭 점은 최대 {options.Value.MaxPoints}개까지입니다.");
            if (req.Points.All(p => !p.Foreground))
                throw ApiException.BadRequest(ErrorCodes.Validation, "포함할 점(전경)이 적어도 하나 필요합니다.");

            var image = await pool.GetAsync(req.ImageId, ct);
            var clicks = req.Points
                .Select(p => new SamClick(Math.Clamp(p.X, 0, 1), Math.Clamp(p.Y, 0, 1), p.Foreground))
                .ToList();

            var result = await sam.PredictAsync(image.Sha256, token => OpenAsync(pool, req.ImageId, token), clicks, ct);
            if (result.Shape is not { } shape)
                return Results.Ok(new SamPredictResponse(null, result.Message));

            var mask = new SamMaskDto(
                shape.Polygon.Select(p => new[] { p.X, p.Y }).ToArray(),
                shape.Box.X, shape.Box.Y, shape.Box.Width, shape.Box.Height,
                result.Score, shape.PixelArea);
            return Results.Ok(new SamPredictResponse(mask));
        }).RequireAuthorization(Policies.Labeler);

        return api;
    }

    /// <summary>
    /// 인코더에는 축소본을 준다. SAM 이 어차피 긴 변 1024 로 줄이므로 원본을 읽을 이유가 없고,
    /// 2448×2048 원본 디코딩을 매번 하지 않아 준비가 눈에 띄게 빨라진다.
    /// </summary>
    private static async Task<Stream> OpenAsync(ImagePoolService pool, Guid imageId, CancellationToken ct)
    {
        var (stream, _, _, _) = await pool.OpenAsync(imageId, "view", ct);
        return stream;
    }
}
