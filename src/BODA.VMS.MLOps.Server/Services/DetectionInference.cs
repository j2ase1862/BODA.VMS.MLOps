using BODA.VMS.MLOps.Contracts;
using BODA.VMS.MLOps.Core.Domain;
using BODA.VMS.MLOps.Core.Inference;
using BODA.VMS.MLOps.Server.Data.Entities;
using BODA.VMS.MLOps.Server.Storage;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using SkiaSharp;

namespace BODA.VMS.MLOps.Server.Services;

/// <summary>
/// 레지스트리에 등록된 검출 모델(YOLO · D-FINE)을 사진 한 장에 돌린다.
/// 사전 라벨링(<see cref="PrelabelService"/>)과 모델 돌려 보기(<see cref="ModelTryService"/>)가 같은 추론을 쓴다 —
/// 둘이 다르게 돌면 "돌려 보기에서는 잡혔는데 미리 채우기에서는 안 잡힌다" 가 생긴다.
///
/// <para><b>전처리와 입력은 규약마다 다르고, 규약은 런타임(VMS)이 정한다.</b>
/// YOLO 는 레터박스(<see cref="LetterBox"/>) + RGB 0~1. D-FINE 은 비율을 무시하고 늘려 맞춘 리사이즈 + RGB 0~1 에
/// 원본 크기 <c>orig_target_sizes</c>(h, w, int64)를 함께 넣고, 상자를 원본 픽셀 xyxy 로 받는다
/// (VMS <c>DFineOnnxEngine</c>, <c>train_dfine.py</c> export). 크기 입력을 빠뜨리면 세션 실행이 실패해,
/// 한동안 D-FINE 모델의 사전 라벨링이 모든 사진을 건너뛰었다.</para>
/// <para>여기서 여는 모델은 업로드 검증을 이미 통과한 레지스트리 아티팩트다. 업로드 경로에서는 세션을 만들지 않는다
/// (판별은 <c>OnnxSafeReader</c> 가 protobuf 만 읽는다).</para>
/// </summary>
public sealed class DetectionInference(IArtifactStorage storage)
{
    /// <summary>검출 모델 기본 입력 크기 (메타에 imgsz 가 없을 때)</summary>
    public const int DefaultInputSize = 640;

    public static bool Supports(ModelFormat format) => format is ModelFormat.Yolo or ModelFormat.DFine;

    public static int InputSizeOf(ModelVersion version) => version.InputSize is > 0 ? version.InputSize.Value : DefaultInputSize;

    public InferenceSession Open(ModelVersion version)
    {
        var artifact = storage.LocalPath(version.ArtifactKey);
        if (artifact is null || !File.Exists(artifact))
            throw ApiException.NotFound("모델 아티팩트 파일");

        var options = new Microsoft.ML.OnnxRuntime.SessionOptions
        {
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
        };
        try
        {
            return new InferenceSession(artifact, options);
        }
        catch (Exception ex)
        {
            // 손상된 ONNX 가 네이티브 예외로 올라와도 요청은 ApiError 로 끝나야 한다
            throw ApiException.BadRequest(ErrorCodes.Validation, $"모델을 여는 데 실패했습니다: {ex.Message}");
        }
    }

    /// <param name="iou">YOLO 의 NMS 문턱. D-FINE 은 NMS 를 쓰지 않는다.</param>
    public async Task<List<Detection>> InferAsync(
        InferenceSession session, ModelFormat format, Image image, int inputSize,
        double confidence, double iou, CancellationToken ct)
    {
        await using var stream = storage.OpenRead(image.StorageKey);
        using var memory = new MemoryStream();
        await stream.CopyToAsync(memory, ct);
        memory.Position = 0;

        return await Task.Run(() =>
        {
            using var bitmap = SKBitmap.Decode(memory)
                ?? throw new ImageProcessor.UnsupportedImageException("이미지를 디코딩하지 못했습니다.");

            if (format != ModelFormat.DFine)
            {
                var box = LetterBox.For(bitmap.Width, bitmap.Height, inputSize);
                var inputName = session.InputMetadata.Keys.First();
                using var yolo = session.Run([NamedOnnxValue.CreateFromTensor(inputName, BuildInput(bitmap, box))]);
                return ReadYolo(yolo, box, confidence, iou);
            }

            // D-FINE — 크기 입력은 이름으로, 없으면 int64 입력으로 찾는다 (런타임과 같은 규칙)
            var names = session.InputMetadata.Keys.ToList();
            var sizesInput = names.FirstOrDefault(n => n == "orig_target_sizes")
                             ?? names.FirstOrDefault(n => session.InputMetadata[n].ElementType == typeof(long));
            var imagesInput = names.FirstOrDefault(n => n != sizesInput) ?? names[0];

            var inputs = new List<NamedOnnxValue> { NamedOnnxValue.CreateFromTensor(imagesInput, BuildStretched(bitmap, inputSize)) };
            if (sizesInput is not null)
            {
                var sizes = new DenseTensor<long>([1, 2]);
                sizes[0, 0] = bitmap.Height;   // (h, w) — 순서가 바뀌면 정사각형이 아닌 사진에서 상자가 어긋난다
                sizes[0, 1] = bitmap.Width;
                inputs.Add(NamedOnnxValue.CreateFromTensor(sizesInput, sizes));
            }
            using var results = session.Run(inputs);
            return ReadDFine(results, bitmap.Width, bitmap.Height, confidence, hasSizesInput: sizesInput is not null);
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

    /// <summary>D-FINE 규약 — 비율을 무시하고 입력 크기로 늘려 맞춘다. 여백 없음, RGB 0~1.</summary>
    private static DenseTensor<float> BuildStretched(SKBitmap source, int size)
    {
        var info = new SKImageInfo(size, size, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        using var resized = source.Resize(info, SKFilterQuality.Medium)
            ?? throw new ImageProcessor.UnsupportedImageException("이미지를 모델 입력 크기로 줄이지 못했습니다.");

        var tensor = new DenseTensor<float>([1, 3, size, size]);
        var buffer = tensor.Buffer.Span;
        int plane = size * size;
        var pixels = resized.GetPixelSpan();
        int stride = resized.RowBytes;
        for (int y = 0; y < size; y++)
        {
            int row = y * stride;
            for (int x = 0; x < size; x++)
            {
                int source4 = row + x * 4;
                int destination = y * size + x;
                buffer[destination] = pixels[source4] / 255f;
                buffer[plane + destination] = pixels[source4 + 1] / 255f;
                buffer[2 * plane + destination] = pixels[source4 + 2] / 255f;
            }
        }
        return tensor;
    }

    private static List<Detection> ReadYolo(
        IDisposableReadOnlyCollection<DisposableNamedOnnxValue> results, LetterBox box, double confidence, double iou)
    {
        var output = results.FirstOrDefault(r => r.Name == "output0")?.AsTensor<float>()
                     ?? results.First().AsTensor<float>();
        var dimensions = output.Dimensions;
        if (dimensions.Length != 3) return [];

        // [1, 4+nc, anchors]
        return DetectionPostProcess.Yolo(output.ToArray(), dimensions[1], dimensions[2], box, confidence, iou);
    }

    private static List<Detection> ReadDFine(
        IDisposableReadOnlyCollection<DisposableNamedOnnxValue> results,
        int width, int height, double confidence, bool hasSizesInput)
    {
        var byName = results.ToDictionary(r => r.Name);
        if (byName.ContainsKey("logits") && byName.ContainsKey("pred_boxes"))
            // 0건으로 조용히 끝내면 "모델이 아무것도 못 찾는다" 로 읽힌다 — 못 읽는다고 말한다
            throw new NotSupportedException("HF 원본 export(logits · pred_boxes) D-FINE 은 아직 읽지 못합니다. train_dfine.py 의 deploy export 를 쓰세요.");

        // 이름이 다른 deploy export 는 위치로 (labels, boxes, scores) — 런타임과 같은 규칙
        var ordered = results.ToList();
        var labelsValue = byName.GetValueOrDefault("labels") ?? (ordered.Count >= 3 ? ordered[0] : null);
        var boxesValue = byName.GetValueOrDefault("boxes") ?? (ordered.Count >= 3 ? ordered[1] : null);
        var scoresValue = byName.GetValueOrDefault("scores") ?? (ordered.Count >= 3 ? ordered[2] : null);
        if (labelsValue is null || boxesValue is null || scoresValue is null) return [];

        long[] labels = labelsValue.Value switch
        {
            Tensor<long> l => l.ToArray(),
            Tensor<int> i => i.ToArray().Select(v => (long)v).ToArray(),
            _ => [],
        };
        var boxes = boxesValue.AsTensor<float>().ToArray();
        var scores = scoresValue.AsTensor<float>().ToArray();
        int count = scores.Length;

        // 크기 입력이 없는 export 는 상자를 0~1 로 낼 수 있다 — 그때는 원본 크기로 나누지 않는다
        bool normalized = !hasSizesInput && boxes.Length > 0 && boxes.All(v => v <= 1.0001f);
        return DetectionPostProcess.DFine(
            labels, boxes, scores, count, normalized ? 1 : width, normalized ? 1 : height, confidence);
    }
}
