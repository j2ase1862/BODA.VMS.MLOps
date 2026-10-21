using System.Diagnostics;
using BODA.VMS.MLOps.Contracts;
using BODA.VMS.MLOps.Contracts.Models;
using BODA.VMS.MLOps.Core.Domain;
using BODA.VMS.MLOps.Server.Data;
using Microsoft.EntityFrameworkCore;

namespace BODA.VMS.MLOps.Server.Services;

/// <summary>
/// 모델 돌려 보기 (개발 문서 §5.5 3번). 승격은 사람이 결정하는데, 그 사람이 지표 숫자만 보고 누르지 않게
/// 사진 위에 결과를 겹쳐 보여 줄 재료를 낸다. WPF 도구의 추론 미리보기를 옮긴 것이다.
///
/// <para><b>읽기만 한다.</b> 라벨을 채우지 않고(그건 <see cref="PrelabelService"/>), 결과를 저장하지도 않는다.</para>
/// <para><b>검출만 지원한다.</b> 사전 라벨링과 같은 추론(<see cref="DetectionInference"/>)을 쓴다 —
/// 분류·이상탐지·OCR 은 후처리 규약을 확인한 실제 모델이 없어, 확인하지 못한 결과를 보여 주느니 거절한다.</para>
/// <para>신뢰도는 낮은 바닥값까지 담아 보내 화면이 문턱을 바로 바꾸게 한다. YOLO 의 NMS 는 느슨하게(<see cref="NmsIou"/>)
/// 걸어 보내고, 화면이 그보다 낮은 IoU 로 더 거른다 — 슬라이더를 움직일 때마다 서버를 부르지 않게.</para>
/// </summary>
public sealed class ModelTryService(MlopsDbContext db, DetectionInference inference, ILogger<ModelTryService> logger)
{
    /// <summary>한 번에 돌리는 사진 수 상한. 서버 CPU 로 돌므로 요청 하나가 오래 붙잡지 않게 한다.</summary>
    public const int MaxImages = 24;
    public const double ScoreFloor = 0.05;
    public const double NmsIou = 0.7;

    public async Task<TryModelResultDto> RunAsync(Guid versionId, TryModelRequest req, CancellationToken ct)
    {
        var version = await db.ModelVersions.AsNoTracking().Include(v => v.Model)
                          .FirstOrDefaultAsync(v => v.Id == versionId, ct)
                      ?? throw ApiException.NotFound("모델 버전");

        if (version.Model.TaskType != TaskType.Detection || !DetectionInference.Supports(version.Format))
            throw ApiException.BadRequest(ErrorCodes.Validation,
                $"돌려 보기는 지금 검출 모델(YOLO · D-FINE)만 지원합니다. 이 버전은 {version.Model.TaskType} · {version.Format} 입니다.");

        var ids = (req.ImageIds ?? []).Distinct().ToArray();
        if (ids.Length == 0)
            throw ApiException.BadRequest(ErrorCodes.Validation, "돌려 볼 사진을 고르세요.");
        if (ids.Length > MaxImages)
            throw ApiException.BadRequest(ErrorCodes.Validation, $"한 번에 {MaxImages}장까지 돌릴 수 있습니다.");

        var images = await db.Images.AsNoTracking().Where(i => ids.Contains(i.Id)).ToDictionaryAsync(i => i.Id, ct);
        var classes = Mapping.Json(version.ClassesJson, Array.Empty<string>());
        int inputSize = DetectionInference.InputSizeOf(version);
        double? nms = version.Format == ModelFormat.Yolo ? NmsIou : null;

        var watch = Stopwatch.StartNew();
        var results = new List<TryImageResultDto>(ids.Length);
        using (var session = inference.Open(version))
        {
            foreach (var id in ids)   // 고른 순서대로 돌려준다 — 화면의 격자 순서와 같게
            {
                ct.ThrowIfCancellationRequested();
                if (!images.TryGetValue(id, out var image))
                {
                    results.Add(new TryImageResultDto(id, [], null, "사진이 없습니다."));
                    continue;
                }
                try
                {
                    var found = await inference.InferAsync(session, version.Format, image, inputSize, ScoreFloor, nms ?? 1.0, ct);
                    var detections = found
                        .OrderByDescending(d => d.Score)
                        .Select(d => new TryDetectionDto(d.ClassIndex,
                            d.ClassIndex >= 0 && d.ClassIndex < classes.Length ? classes[d.ClassIndex] : $"#{d.ClassIndex}",
                            Math.Round(d.Score, 4), d.Box.X, d.Box.Y, d.Box.Width, d.Box.Height))
                        .ToList();
                    results.Add(new TryImageResultDto(id, detections, detections.Count == 0 ? null : detections[0].Score));
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // 한 장이 깨져도 나머지는 보여 준다
                    logger.LogWarning(ex, "돌려 보기 실패 {ImageId}", id);
                    results.Add(new TryImageResultDto(id, [], null, ex.Message));
                }
            }
        }

        logger.LogInformation("돌려 보기 · 모델 버전 {Version} v{Number} · {Count}장 {Elapsed}ms",
            version.Id, version.Number, ids.Length, watch.ElapsedMilliseconds);
        return new TryModelResultDto(version.Id, version.Format, classes, ScoreFloor, nms, results, watch.ElapsedMilliseconds);
    }
}
