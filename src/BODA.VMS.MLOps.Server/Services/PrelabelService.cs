using BODA.VMS.MLOps.Contracts;
using BODA.VMS.MLOps.Core.Domain;
using BODA.VMS.MLOps.Core.Inference;
using BODA.VMS.MLOps.Core.Labeling;
using BODA.VMS.MLOps.Server.Auth;
using BODA.VMS.MLOps.Server.Data;
using BODA.VMS.MLOps.Server.Data.Entities;
using BODA.VMS.MLOps.Server.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using SkiaSharp;

namespace BODA.VMS.MLOps.Server.Services;

/// <summary>사전 라벨링 한 번의 결과</summary>
public sealed record PrelabelResult(
    int Considered, int Inferred, int Filled, int Skipped, int Annotations, string? Message);

/// <summary>
/// 후보 모델로 미라벨 이미지를 훑어 초기 라벨과 불확실도를 채운다 (개발 문서 §5.4 Active Learning).
///
/// <para><b>워커가 아니라 서버가 돌린다.</b>
/// 학습은 GPU 와 파이썬이 필요해 워커의 몫이지만, 추론은 ONNX Runtime 하나면 되고
/// 서버는 SAM 보조 때문에 이미 그것을 들고 있다. 작업 큐·데이터셋 동기화·아티팩트 왕복을
/// 한 벌 더 만드는 대신, 이미 있는 것으로 끝낸다. 사진 수백 장이면 몇십 초다.
/// </para>
/// <para><b>사람이 손댄 이미지는 건드리지 않는다.</b>
/// 실제로 채우는 일은 <see cref="LabelingService.PrefillAsync"/> 가 하고,
/// 거기서 이미 라벨·검토가 끝난 이미지를 걸러 낸다. 여기서는 대상 고르기와 추론만 한다.
/// </para>
/// <para><b>검출만 지원한다.</b>
/// 분류·이상탐지·OCR 은 후처리 규약이 저마다 달라, 지금 넣으면 확인하지 못한 코드가 늘 뿐이다.
/// 그 유형은 이유를 말하며 거절한다.
/// </para>
/// </summary>
public sealed class PrelabelService(
    MlopsDbContext db,
    IArtifactStorage storage,
    LabelingService labeling,
    ILogger<PrelabelService> logger,
    TimeProvider clock)
{
    /// <summary>한 번에 훑을 이미지 수 상한. 요청이 너무 길어지지 않도록 나눠 부르게 한다.</summary>
    public const int MaxImagesPerCall = 500;

    /// <summary>검출 모델 기본 입력 크기 (메타에 imgsz 가 없을 때)</summary>
    private const int DefaultInputSize = 640;

    public async Task<PrelabelResult> RunAsync(
        Guid datasetId, Guid modelVersionId, double confidence, int maxImages,
        CurrentUser user, CancellationToken ct)
    {
        var dataset = await db.Datasets.AsNoTracking().FirstOrDefaultAsync(d => d.Id == datasetId, ct)
                      ?? throw ApiException.NotFound("데이터셋");
        var version = await db.ModelVersions.AsNoTracking().Include(v => v.Model)
                          .FirstOrDefaultAsync(v => v.Id == modelVersionId, ct)
                      ?? throw ApiException.NotFound("모델 버전");

        // 데이터셋 유형을 먼저 본다. 분류 데이터셋이면 어떤 모델을 골랐든 이유가 같아서,
        // "모델이 안 맞는다" 보다 "아직 지원하지 않는다" 가 사람에게 쓸모 있는 말이다.
        if (dataset.TaskType != TaskType.Detection)
            throw ApiException.BadRequest(ErrorCodes.Validation,
                "사전 라벨링은 지금 검출 데이터셋만 지원합니다. 분류·이상탐지·OCR 은 아직 없습니다.");
        if (version.Model.TaskType != dataset.TaskType)
            throw ApiException.BadRequest(ErrorCodes.TaskTypeMismatch,
                $"데이터셋은 {dataset.TaskType} 인데 모델은 {version.Model.TaskType} 입니다.");
        if (version.Format is not (ModelFormat.Yolo or ModelFormat.DFine))
            throw ApiException.BadRequest(ErrorCodes.Validation,
                $"이 규약({version.Format})은 사전 라벨링을 아직 지원하지 않습니다. YOLO 또는 D-FINE 이어야 합니다.");

        var artifact = storage.LocalPath(version.ArtifactKey);
        if (artifact is null || !File.Exists(artifact))
            throw ApiException.NotFound("모델 아티팩트 파일");

        // 모델의 클래스 이름을 데이터셋 클래스에 맞춘다. 없는 이름은 버린다 —
        // 이름이 다른 모델을 잘못 골랐을 때 조용히 엉뚱한 라벨이 붙는 것을 막는다.
        var modelClasses = Mapping.Json(version.ClassesJson, Array.Empty<string>());
        var datasetClasses = DatasetService.ClassesOf(dataset);
        var shared = modelClasses.Where(c => datasetClasses.Contains(c, StringComparer.Ordinal)).ToArray();
        if (shared.Length == 0)
            throw ApiException.BadRequest(ErrorCodes.ClassMismatch,
                $"모델 클래스({string.Join(", ", modelClasses)})가 데이터셋 클래스({string.Join(", ", datasetClasses)})와 하나도 겹치지 않습니다.");

        var targets = await UnlabeledImagesAsync(datasetId, Math.Clamp(maxImages, 1, MaxImagesPerCall), ct);
        if (targets.Count == 0)
            return new PrelabelResult(0, 0, 0, 0, 0, "채울 미라벨 이미지가 없습니다.");

        int inputSize = version.InputSize is > 0 ? version.InputSize.Value : DefaultInputSize;
        var started = clock.GetTimestamp();

        var predictions = new Dictionary<Guid, IReadOnlyList<LabelAnnotation>>();
        var uncertainty = new Dictionary<Guid, double>();
        int skipped = 0, annotations = 0;

        using (var session = OpenSession(artifact))
        {
            foreach (var image in targets)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    var found = await InferAsync(session, version.Format, image, inputSize, confidence, ct);
                    var labels = found
                        .Where(d => d.ClassIndex >= 0 && d.ClassIndex < modelClasses.Length)
                        .Select(d => new { Detection = d, Name = modelClasses[d.ClassIndex] })
                        .Where(x => datasetClasses.Contains(x.Name, StringComparer.Ordinal))
                        .Select(x => new LabelAnnotation
                        {
                            Shape = AnnotationShape.Box,
                            ClassName = x.Name,
                            Box = x.Detection.Box.Clamped(),
                        })
                        .Where(a => a.IsValid(out _))
                        .ToList();

                    predictions[image.Id] = labels;
                    uncertainty[image.Id] = UncertaintyScore.ForDetections(found);
                    annotations += labels.Count;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // 한 장이 깨져도 나머지는 계속 본다. 못 본 장은 그대로 미라벨로 남는다.
                    logger.LogWarning(ex, "사전 라벨링 실패 {ImageId}", image.Id);
                    skipped++;
                }
            }
        }

        int filled = await labeling.PrefillAsync(datasetId, predictions, uncertainty, user, ct);

        var elapsed = clock.GetElapsedTime(started);
        logger.LogInformation("사전 라벨링 {Dataset} · 모델 v{Number} · {Inferred}장 {Elapsed:0.0}초 · 채움 {Filled}",
            datasetId, version.Number, predictions.Count, elapsed.TotalSeconds, filled);

        var message = shared.Length < modelClasses.Length
            ? $"모델 클래스 {modelClasses.Length}개 중 {shared.Length}개만 데이터셋에 있어 나머지 예측은 버렸습니다."
            : null;
        return new PrelabelResult(targets.Count, predictions.Count, filled, skipped, annotations, message);
    }

    /// <summary>
    /// 아직 사람이 손대지 않은 이미지만 고른다. 이미 라벨·검토가 끝난 것은 대상이 아니고,
    /// 남이 잠가 둔 것도 건드리지 않는다.
    /// </summary>
    private async Task<List<Image>> UnlabeledImagesAsync(Guid datasetId, int take, CancellationToken ct)
    {
        var memberIds = await db.DatasetImages.AsNoTracking()
            .Where(m => m.DatasetId == datasetId).Select(m => m.ImageId).ToListAsync(ct);
        if (memberIds.Count == 0) return [];

        var states = await db.ImageLabelStates.AsNoTracking()
            .Where(s => s.DatasetId == datasetId).ToDictionaryAsync(s => s.ImageId, ct);

        var now = clock.GetUtcNow().UtcDateTime;
        var wanted = memberIds.Where(id =>
        {
            if (!states.TryGetValue(id, out var state)) return true;
            if (state.Status is LabelStatus.Labeled or LabelStatus.Reviewed) return false;
            if (state.AnnotationCount > 0) return false;
            return state.LockExpiresAt is null || state.LockExpiresAt <= now;
        }).Take(take).ToList();

        if (wanted.Count == 0) return [];
        var images = await db.Images.AsNoTracking().Where(i => wanted.Contains(i.Id)).ToListAsync(ct);
        return images;
    }

    private InferenceSession OpenSession(string artifactPath)
    {
        var options = new Microsoft.ML.OnnxRuntime.SessionOptions
        {
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
        };
        try
        {
            return new InferenceSession(artifactPath, options);
        }
        catch (Exception ex)
        {
            // 손상된 ONNX 가 네이티브 예외로 올라와도 요청은 ApiError 로 끝나야 한다
            throw ApiException.BadRequest(ErrorCodes.Validation, $"모델을 여는 데 실패했습니다: {ex.Message}");
        }
    }

    private async Task<List<Detection>> InferAsync(
        InferenceSession session, ModelFormat format, Image image, int inputSize,
        double confidence, CancellationToken ct)
    {
        await using var stream = storage.OpenRead(image.StorageKey);
        using var memory = new MemoryStream();
        await stream.CopyToAsync(memory, ct);
        memory.Position = 0;

        return await Task.Run(() =>
        {
            using var bitmap = SKBitmap.Decode(memory)
                ?? throw new ImageProcessor.UnsupportedImageException("이미지를 디코딩하지 못했습니다.");

            var box = LetterBox.For(bitmap.Width, bitmap.Height, inputSize);
            var tensor = BuildInput(bitmap, box);

            var inputName = session.InputMetadata.Keys.First();
            using var results = session.Run([NamedOnnxValue.CreateFromTensor(inputName, tensor)]);

            return format == ModelFormat.DFine
                ? ReadDFine(results, bitmap.Width, bitmap.Height, confidence)
                : ReadYolo(results, box, confidence);
        }, ct);
    }

    /// <summary>
    /// 레터박스로 줄여 넣고 남는 자리는 회색(114)으로 채운다. 값은 0~1 로 나눈다 (Ultralytics 규약).
    /// </summary>
    private static DenseTensor<float> BuildInput(SKBitmap source, LetterBox box)
    {
        var info = new SKImageInfo(box.ScaledWidth, box.ScaledHeight, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        using var resized = source.Resize(info, SKFilterQuality.Medium)
            ?? throw new ImageProcessor.UnsupportedImageException("이미지를 모델 입력 크기로 줄이지 못했습니다.");

        int size = box.InputSize;
        var tensor = new DenseTensor<float>([1, 3, size, size]);
        var buffer = tensor.Buffer.Span;
        int plane = size * size;

        const float pad = 114f / 255f;
        buffer.Fill(pad);

        var pixels = resized.GetPixelSpan();
        int stride = resized.RowBytes;
        for (int y = 0; y < box.ScaledHeight; y++)
        {
            int row = y * stride;
            int destinationRow = (y + box.PadY) * size + box.PadX;
            for (int x = 0; x < box.ScaledWidth; x++)
            {
                int source4 = row + x * 4;
                int destination = destinationRow + x;
                buffer[destination] = pixels[source4] / 255f;
                buffer[plane + destination] = pixels[source4 + 1] / 255f;
                buffer[2 * plane + destination] = pixels[source4 + 2] / 255f;
            }
        }
        return tensor;
    }

    private static List<Detection> ReadYolo(
        IDisposableReadOnlyCollection<DisposableNamedOnnxValue> results, LetterBox box, double confidence)
    {
        var output = results.FirstOrDefault(r => r.Name == "output0")?.AsTensor<float>()
                     ?? results.First().AsTensor<float>();
        var dimensions = output.Dimensions;
        if (dimensions.Length != 3) return [];

        // [1, 4+nc, anchors]
        return DetectionPostProcess.Yolo(output.ToArray(), dimensions[1], dimensions[2], box, confidence);
    }

    private static List<Detection> ReadDFine(
        IDisposableReadOnlyCollection<DisposableNamedOnnxValue> results,
        int width, int height, double confidence)
    {
        var labels = results.FirstOrDefault(r => r.Name == "labels")?.AsTensor<long>();
        var boxes = results.FirstOrDefault(r => r.Name == "boxes")?.AsTensor<float>();
        var scores = results.FirstOrDefault(r => r.Name == "scores")?.AsTensor<float>();
        if (labels is null || boxes is null || scores is null) return [];

        int count = labels.Dimensions.Length >= 2 ? labels.Dimensions[1] : labels.Dimensions[0];
        return DetectionPostProcess.DFine(
            labels.ToArray(), boxes.ToArray(), scores.ToArray(), count, width, height, confidence);
    }
}
