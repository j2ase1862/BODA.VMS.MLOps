using BODA.VMS.MLOps.Contracts;
using BODA.VMS.MLOps.Core.Sam;
using Microsoft.Extensions.Options;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using SkiaSharp;

namespace BODA.VMS.MLOps.Server.Services.Sam;

/// <summary>SAM 이 돌려준 결과. 도형이 없으면 <see cref="Shape"/> 가 null.</summary>
public sealed record SamResult(MaskShape? Shape, double Score, string? Message);

/// <summary>
/// SAM 보조 라벨링 — 클릭 한 번으로 객체 폴리곤을 만든다 (개발 문서 §5.4).
///
/// <para><b>인코더·디코더를 모두 서버에서 돌린다.</b>
/// 설계 문서는 디코더를 브라우저 onnxruntime-web 으로 돌리는 그림을 그렸지만, 이 구성에서는
/// 서버에서 함께 돌리는 편이 낫다고 보고 그렇게 했다.
/// 브라우저 디코더는 첫 클릭 전에 onnxruntime-web(약 10MB)과 디코더 ONNX(약 33MB)를 받아야 하고,
/// 이미지마다 임베딩 [1,256,64,64] float = 4MB 를 따로 내려보내야 한다.
/// 폐쇄망 현장에서 사진 한 장 볼 때마다 4MB 를 더 보내는 대신, 사내망 왕복 한 번(수십 ms)으로
/// 2KB 짜리 폴리곤만 받는다. 디코더 계산 자체는 인코더의 수백 분의 일이라 서버 부담도 거의 없다.
/// 나중에 브라우저 디코더로 옮기고 싶으면 임베딩을 그대로 내려보내는 엔드포인트만 더하면 된다.
/// </para>
/// <para><b>임베딩 캐시.</b>
/// 무거운 쪽은 인코더다(1024×1024 한 번). 같은 사진을 여러 번 클릭하는 것이 라벨링의 기본 동작이라
/// 이미지 해시로 임베딩을 메모리에 들고 있는다. 디스크에는 두지 않는다 — 한 장에 4MB 라
/// 이미지 만 장이면 40GB 가 되고, 그만큼을 아끼자고 정리 정책을 하나 더 만들 이유가 없다.
/// </para>
/// </summary>
public sealed class SamAssistService : IDisposable
{
    private readonly SamOptions _options;
    private readonly ILogger<SamAssistService> _logger;
    private readonly TimeProvider _clock;
    private readonly string _basePath;

    private readonly SemaphoreSlim _loadGate = new(1, 1);
    // 인코더는 한 번에 하나만 — 동시에 여럿 돌리면 메모리만 몇 배로 먹고 전체가 더 느려진다
    private readonly SemaphoreSlim _encodeGate = new(1, 1);
    private readonly SemaphoreSlim _decodeGate = new(2, 2);

    private InferenceSession? _encoder;
    private InferenceSession? _decoder;
    private volatile string? _loadFailure;
    private DateTimeOffset _loadFailedAt;

    private readonly object _cacheLock = new();
    private readonly Dictionary<string, CachedEmbedding> _cache = [];
    private readonly List<string> _cacheOrder = [];

    private sealed record CachedEmbedding(float[] Data, int[] Shape, SamGeometry Geometry);

    public SamAssistService(IOptions<SamOptions> options, ILogger<SamAssistService> logger,
        TimeProvider clock, IHostEnvironment environment)
    {
        _options = options.Value;
        _logger = logger;
        _clock = clock;
        _basePath = environment.ContentRootPath;
    }

    /// <summary>설정과 파일이 갖춰졌는지. 이게 false 면 화면에서 SAM 을 아예 숨긴다.</summary>
    public bool Available =>
        _options.Enabled
        && _options.ResolvedEncoderPath(_basePath) is { } encoder && File.Exists(encoder)
        && _options.ResolvedDecoderPath(_basePath) is { } decoder && File.Exists(decoder);

    public (bool Available, bool Ready, string? Message) Status()
    {
        if (!_options.Enabled)
            return (false, false, "SAM 보조가 설정에서 꺼져 있습니다 (Sam:Enabled).");

        var encoder = _options.ResolvedEncoderPath(_basePath);
        var decoder = _options.ResolvedDecoderPath(_basePath);
        if (encoder is null || decoder is null)
            return (false, false, "SAM 모델 경로가 설정되지 않았습니다 (Sam:EncoderPath · Sam:DecoderPath).");
        if (!File.Exists(encoder)) return (false, false, $"인코더 파일이 없습니다: {encoder}");
        if (!File.Exists(decoder)) return (false, false, $"디코더 파일이 없습니다: {decoder}");

        if (_loadFailure is { } failure) return (true, false, failure);
        return (true, _encoder is not null && _decoder is not null, null);
    }

    /// <summary>
    /// 임베딩을 미리 만들어 둔다. 라벨링 화면이 이미지를 열 때 부르면
    /// 사용자가 처음 클릭할 즈음에는 이미 준비돼 있다.
    /// </summary>
    public async Task<bool> PrepareAsync(string cacheKey, Func<CancellationToken, Task<Stream>> open, CancellationToken ct)
    {
        if (!Available) return false;
        try
        {
            await GetEmbeddingAsync(cacheKey, open, ct);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "SAM 임베딩 준비 실패 {Key}", cacheKey);
            return false;
        }
    }

    /// <summary>클릭 점들로 마스크를 만들고 폴리곤으로 접어 돌려준다.</summary>
    public async Task<SamResult> PredictAsync(
        string cacheKey, Func<CancellationToken, Task<Stream>> open,
        IReadOnlyList<SamClick> clicks, CancellationToken ct)
    {
        if (clicks.Count == 0) return new SamResult(null, 0, "클릭한 점이 없습니다.");

        var embedding = await GetEmbeddingAsync(cacheKey, open, ct);
        var decoder = _decoder ?? throw new InvalidOperationException("디코더가 준비되지 않았습니다.");

        await _decodeGate.WaitAsync(ct);
        try
        {
            return await Task.Run(() => Decode(decoder, embedding, clicks), ct);
        }
        finally { _decodeGate.Release(); }
    }

    // ───────────── 디코더 ─────────────

    private static SamResult Decode(InferenceSession decoder, CachedEmbedding embedding, IReadOnlyList<SamClick> clicks)
    {
        var geometry = embedding.Geometry;

        // 공식 ONNX 예제와 같이 (0,0) 라벨 -1 짜리 자리 채움 점을 하나 붙인다.
        // 박스 프롬프트 없이 점만 줄 때 이 점이 있어야 프롬프트 인코더가 학습 때와 같은 모양을 받는다.
        int count = clicks.Count + 1;
        var coords = new DenseTensor<float>([1, count, 2]);
        var labels = new DenseTensor<float>([1, count]);
        for (int i = 0; i < clicks.Count; i++)
        {
            var (x, y) = geometry.ToInputSpace(clicks[i].X, clicks[i].Y);
            coords[0, i, 0] = x;
            coords[0, i, 1] = y;
            labels[0, i] = clicks[i].Label;
        }
        coords[0, clicks.Count, 0] = 0f;
        coords[0, clicks.Count, 1] = 0f;
        labels[0, clicks.Count] = -1f;

        var (maskHeight, maskWidth) = geometry.MaskSize();
        var originalSize = new DenseTensor<float>([2]);
        originalSize[0] = maskHeight;
        originalSize[1] = maskWidth;

        var inputs = new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor("image_embeddings", new DenseTensor<float>(embedding.Data, embedding.Shape)),
            NamedOnnxValue.CreateFromTensor("point_coords", coords),
            NamedOnnxValue.CreateFromTensor("point_labels", labels),
            NamedOnnxValue.CreateFromTensor("mask_input", new DenseTensor<float>([1, 1, 256, 256])),
            NamedOnnxValue.CreateFromTensor("has_mask_input", new DenseTensor<float>([1])),
            NamedOnnxValue.CreateFromTensor("orig_im_size", originalSize),
        };

        using var results = decoder.Run(inputs);

        var masks = results.First(r => r.Name == "masks").AsTensor<float>();
        var dimensions = masks.Dimensions;
        if (dimensions.Length != 4) return new SamResult(null, 0, "디코더가 예상과 다른 모양을 돌려주었습니다.");

        int candidates = dimensions[1], height = dimensions[2], width = dimensions[3];

        // 여러 후보가 나오면 모델이 매긴 IoU 가 가장 높은 것을 쓴다
        int best = 0;
        double score = 0;
        var iou = results.FirstOrDefault(r => r.Name == "iou_predictions")?.AsTensor<float>();
        if (iou is not null && iou.Dimensions.Length == 2 && iou.Dimensions[1] == candidates)
        {
            for (int i = 0; i < candidates; i++)
                if (iou[0, i] > score) { score = iou[0, i]; best = i; }
        }

        // DenseTensor 는 NCHW 연속이라 원하는 후보 한 장을 잘라 그대로 넘긴다
        var flat = masks.ToArray();
        int plane = height * width;
        var slice = new ReadOnlySpan<float>(flat, best * plane, plane);

        // 모델의 IoU 예측은 회귀 값이라 1 을 살짝 넘기도 한다. 화면에 101% 로 보이지 않게 여기서 자른다.
        score = Math.Clamp(score, 0, 1);

        var shape = MaskContour.FromLogits(slice, width, height);
        return shape is null
            ? new SamResult(null, score, "이 위치에서 객체를 찾지 못했습니다. 다른 곳을 클릭하거나 점을 더 찍어 보세요.")
            : new SamResult(shape, score, null);
    }

    // ───────────── 인코더·캐시 ─────────────

    private async Task<CachedEmbedding> GetEmbeddingAsync(
        string cacheKey, Func<CancellationToken, Task<Stream>> open, CancellationToken ct)
    {
        if (TryGetCached(cacheKey) is { } cached) return cached;

        await EnsureLoadedAsync(ct);
        var encoder = _encoder ?? throw new InvalidOperationException("인코더가 준비되지 않았습니다.");

        await _encodeGate.WaitAsync(ct);
        try
        {
            // 기다리는 사이에 다른 요청이 만들어 놨을 수 있다
            if (TryGetCached(cacheKey) is { } raced) return raced;

            await using var stream = await open(ct);
            using var memory = new MemoryStream();
            await stream.CopyToAsync(memory, ct);
            memory.Position = 0;

            var started = _clock.GetTimestamp();
            var result = await Task.Run(() => Encode(encoder, memory), ct);
            _logger.LogDebug("SAM 임베딩 {Key} {Elapsed:0} ms", cacheKey,
                _clock.GetElapsedTime(started).TotalMilliseconds);

            Store(cacheKey, result);
            return result;
        }
        finally { _encodeGate.Release(); }
    }

    private static CachedEmbedding Encode(InferenceSession encoder, Stream image)
    {
        using var bitmap = SKBitmap.Decode(image)
            ?? throw new ImageProcessor.UnsupportedImageException("이미지를 디코딩하지 못했습니다.");

        var geometry = SamGeometry.For(bitmap.Width, bitmap.Height);
        var tensor = BuildInputTensor(bitmap, geometry);

        var inputName = encoder.InputMetadata.Keys.First();
        using var results = encoder.Run([NamedOnnxValue.CreateFromTensor(inputName, tensor)]);
        var output = results.First().AsTensor<float>();
        return new CachedEmbedding(output.ToArray(), output.Dimensions.ToArray(), geometry);
    }

    /// <summary>
    /// SAM 전처리: 긴 변을 1024 로 줄여 왼쪽 위에 붙이고 나머지는 0 으로 둔다.
    /// 정규화한 뒤 남는 자리가 0 이어야 하므로, 채우기가 아니라 "쓰지 않고 두는" 것이 맞다.
    /// </summary>
    private static DenseTensor<float> BuildInputTensor(SKBitmap source, SamGeometry geometry)
    {
        var info = new SKImageInfo(geometry.ScaledWidth, geometry.ScaledHeight,
            SKColorType.Rgba8888, SKAlphaType.Unpremul);
        using var resized = source.Resize(info, SKFilterQuality.High)
            ?? throw new ImageProcessor.UnsupportedImageException("이미지를 SAM 입력 크기로 줄이지 못했습니다.");

        const int size = SamGeometry.InputSize;
        var tensor = new DenseTensor<float>([1, 3, size, size]);
        var buffer = tensor.Buffer.Span;
        int plane = size * size;

        var pixels = resized.GetPixelSpan();   // Rgba8888 → R,G,B,A 순서
        int stride = resized.RowBytes;

        for (int y = 0; y < geometry.ScaledHeight; y++)
        {
            int row = y * stride;
            int destinationRow = y * size;
            for (int x = 0; x < geometry.ScaledWidth; x++)
            {
                int source4 = row + x * 4;
                int destination = destinationRow + x;
                buffer[destination] = SamNormalization.Apply(pixels[source4], 0);
                buffer[plane + destination] = SamNormalization.Apply(pixels[source4 + 1], 1);
                buffer[2 * plane + destination] = SamNormalization.Apply(pixels[source4 + 2], 2);
            }
        }
        return tensor;
    }

    private CachedEmbedding? TryGetCached(string key)
    {
        lock (_cacheLock)
        {
            if (!_cache.TryGetValue(key, out var value)) return null;
            _cacheOrder.Remove(key);
            _cacheOrder.Add(key);
            return value;
        }
    }

    private void Store(string key, CachedEmbedding value)
    {
        lock (_cacheLock)
        {
            _cache[key] = value;
            _cacheOrder.Remove(key);
            _cacheOrder.Add(key);
            int limit = Math.Max(1, _options.MaxCachedEmbeddings);
            while (_cacheOrder.Count > limit)
            {
                _cache.Remove(_cacheOrder[0]);
                _cacheOrder.RemoveAt(0);
            }
        }
    }

    // ───────────── 모델 적재 ─────────────

    private async Task EnsureLoadedAsync(CancellationToken ct)
    {
        if (_encoder is not null && _decoder is not null) return;

        var status = Status();
        if (!status.Available)
            throw ApiException.BadRequest(ErrorCodes.Validation, status.Message ?? "SAM 보조를 쓸 수 없습니다.");

        await _loadGate.WaitAsync(ct);
        try
        {
            if (_encoder is not null && _decoder is not null) return;
            if (_loadFailure is not null
                && _clock.GetUtcNow() - _loadFailedAt < TimeSpan.FromSeconds(_options.RetryAfterFailureSec))
                throw ApiException.BadRequest(ErrorCodes.Validation, _loadFailure);

            var encoderPath = _options.ResolvedEncoderPath(_basePath)!;
            var decoderPath = _options.ResolvedDecoderPath(_basePath)!;

            try
            {
                await Task.Run(() =>
                {
                    var sessionOptions = new Microsoft.ML.OnnxRuntime.SessionOptions { GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL };
                    if (_options.IntraOpThreads > 0) sessionOptions.IntraOpNumThreads = _options.IntraOpThreads;

                    var encoder = new InferenceSession(encoderPath, sessionOptions);
                    InferenceSession decoder;
                    try { decoder = new InferenceSession(decoderPath, sessionOptions); }
                    catch { encoder.Dispose(); throw; }

                    _encoder = encoder;
                    _decoder = decoder;
                }, ct);

                _loadFailure = null;
                _logger.LogInformation("SAM 모델 적재 완료 · 인코더 {Encoder} · 디코더 {Decoder}", encoderPath, decoderPath);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // 손상된 ONNX 가 네이티브 예외로 올라와도 요청 흐름은 ApiError 로 끝나야 한다
                _loadFailure = $"SAM 모델을 여는 데 실패했습니다: {ex.Message}";
                _loadFailedAt = _clock.GetUtcNow();
                _logger.LogError(ex, "SAM 모델 적재 실패 {Encoder} · {Decoder}", encoderPath, decoderPath);
                throw ApiException.BadRequest(ErrorCodes.Validation, _loadFailure);
            }
        }
        finally { _loadGate.Release(); }
    }

    public void Dispose()
    {
        _encoder?.Dispose();
        _decoder?.Dispose();
        _encoder = null;
        _decoder = null;
        _loadGate.Dispose();
        _encodeGate.Dispose();
        _decodeGate.Dispose();
    }
}
