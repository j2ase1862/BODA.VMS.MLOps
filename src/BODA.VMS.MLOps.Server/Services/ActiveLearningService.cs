using System.Diagnostics;
using BODA.VMS.MLOps.Contracts;
using BODA.VMS.MLOps.Contracts.Datasets;
using BODA.VMS.MLOps.Contracts.Models;
using BODA.VMS.MLOps.Core.Domain;
using BODA.VMS.MLOps.Server.Data;
using Microsoft.EntityFrameworkCore;

namespace BODA.VMS.MLOps.Server.Services;

/// <summary>
/// 새 사진 훑기 (Active Learning, 개발 문서 §5.4 · §5.5 4번). WPF 도구의 "폴더를 훑어 실패작 고르기" 를 옮긴 것이다 —
/// 웹에서 폴더에 해당하는 것은 수집 사진(이미지 풀)이다.
///
/// <para><b>훑기만 한다.</b> 데이터셋에 담는 것은 사람이 고른 뒤 따로 부른다(사진 담기 + 고른 사진만 사전 라벨링).
/// 모델이 헷갈린 사진을 자동으로 담으면, 사람이 보지 않은 사진이 학습 데이터로 흘러간다.</para>
/// <para><b>순서는 "모델이 자신 없어 한 사진부터"</b> — 후보가 하나도 없는 것, 그다음 가장 높은 점수가 낮은 순.
/// WPF 의 "신뢰도 낮은 순" 과 같다. 이번 묶음 안에서의 순서이고, 이미지 풀은 최근 것부터 묶음으로 나눠 훑는다.</para>
/// <para>추론은 돌려 보기와 같은 <see cref="DetectionInference"/> 로, 같은 바닥값·NMS 로 돈다.</para>
/// </summary>
public sealed class ActiveLearningService(
    MlopsDbContext db, ImagePoolService pool, DetectionInference inference, ILogger<ActiveLearningService> logger)
{
    /// <summary>한 번에 훑는 사진 수 상한. 서버 CPU 로 돌므로 요청 하나가 너무 오래 붙잡지 않게 한다.</summary>
    public const int MaxPerScan = 60;

    public async Task<ActiveLearningScanResultDto> ScanAsync(Guid datasetId, ActiveLearningScanRequest req, CancellationToken ct)
    {
        var dataset = await db.Datasets.AsNoTracking().FirstOrDefaultAsync(d => d.Id == datasetId, ct)
                      ?? throw ApiException.NotFound("데이터셋");
        var version = await db.ModelVersions.AsNoTracking().Include(v => v.Model)
                          .FirstOrDefaultAsync(v => v.Id == req.ModelVersionId, ct)
                      ?? throw ApiException.NotFound("모델 버전");

        if (dataset.TaskType != TaskType.Detection)
            throw ApiException.BadRequest(ErrorCodes.Validation, "새 사진 훑기는 지금 검출 데이터셋만 지원합니다.");
        if (version.Model.TaskType != TaskType.Detection || !DetectionInference.Supports(version.Format))
            throw ApiException.BadRequest(ErrorCodes.Validation,
                $"새 사진 훑기는 검출 모델(YOLO · D-FINE)로만 합니다. 이 버전은 {version.Model.TaskType} · {version.Format} 입니다.");

        // 이름이 하나도 겹치지 않는 모델로 훑으면 고른 뒤 미리 채울 라벨이 하나도 없다 — 사전 라벨링과 같은 규칙
        var modelClasses = Mapping.Json(version.ClassesJson, Array.Empty<string>());
        var datasetClasses = DatasetService.ClassesOf(dataset);
        if (!modelClasses.Any(c => datasetClasses.Contains(c, StringComparer.Ordinal)))
            throw ApiException.BadRequest(ErrorCodes.ClassMismatch,
                $"모델 클래스({string.Join(", ", modelClasses)})가 데이터셋 클래스({string.Join(", ", datasetClasses)})와 하나도 겹치지 않습니다.");

        int take = Math.Clamp(req.Take, 1, MaxPerScan);
        var (images, total) = await pool.ListAsync(new ImagePoolService.ImageQuery(
            datasetId, InDataset: false, req.Source, LineId: null, req.Tag, Search: null, LabelStatus: null,
            Skip: Math.Max(0, req.Skip), Take: take), ct);

        int inputSize = DetectionInference.InputSizeOf(version);
        double nms = version.Format == ModelFormat.Yolo ? ModelTryService.NmsIou : 1.0;
        var watch = Stopwatch.StartNew();
        var items = new List<ActiveLearningItemDto>(images.Count);

        if (images.Count > 0)
        {
            using var session = inference.Open(version);
            foreach (var image in images)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    var found = await inference.InferAsync(session, version.Format, image, inputSize, ModelTryService.ScoreFloor, nms, ct);
                    var detections = found.OrderByDescending(d => d.Score)
                        .Select(d => new TryDetectionDto(d.ClassIndex,
                            d.ClassIndex >= 0 && d.ClassIndex < modelClasses.Length ? modelClasses[d.ClassIndex] : $"#{d.ClassIndex}",
                            Math.Round(d.Score, 4), d.Box.X, d.Box.Y, d.Box.Width, d.Box.Height))
                        .ToList();
                    items.Add(new ActiveLearningItemDto(image.ToDto(), detections.Count == 0 ? null : detections[0].Score, detections));
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogWarning(ex, "새 사진 훑기 실패 {ImageId}", image.Id);
                    items.Add(new ActiveLearningItemDto(image.ToDto(), null, [], ex.Message));
                }
            }
        }

        // 자신 없어 한 순서: 후보 없음(null) → 최고 점수가 낮은 순. 읽지 못한 사진은 맨 뒤.
        var ordered = items
            .OrderBy(i => i.Error is null ? 0 : 1)
            .ThenBy(i => i.MaxScore ?? -1)
            .ToList();

        logger.LogInformation("새 사진 훑기 {Dataset} · 모델 v{Number} · {Count}장 {Elapsed}ms (풀 {Total}장 중 {Skip}부터)",
            datasetId, version.Number, images.Count, watch.ElapsedMilliseconds, total, req.Skip);
        return new ActiveLearningScanResultDto(version.Id, modelClasses, ModelTryService.ScoreFloor,
            images.Count, total, Math.Max(0, req.Skip) + images.Count, ordered, watch.ElapsedMilliseconds);
    }
}
