using BODA.VMS.MLOps.Contracts;
using BODA.VMS.MLOps.Core.Labeling;
using BODA.VMS.MLOps.Core.Sam;
using Microsoft.Extensions.Options;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using SkiaSharp;

namespace BODA.VMS.MLOps.Server.Services.Sam;

/// <summary>후보 하나. 같은 클릭에 대해 모델이 내놓는 여러 크기의 해석 중 하나다.</summary>
public sealed record SamCandidate(MaskShape Shape, double Score);

/// <summary>
/// SAM 결과. <see cref="Candidates"/> 는 작은 것부터 큰 것 순이고,
/// <see cref="Best"/> 는 모델이 스스로 고른 것의 자리다. 비어 있으면 <see cref="Message"/> 에 이유가 있다.
/// </summary>
public sealed record SamResult(IReadOnlyList<SamCandidate> Candidates, int Best, string? Message);

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
/// <para><b>후보를 여러 개 준다.</b>
/// 클릭 한 번의 뜻은 대개 애매하다. 사선 위를 집었을 때 사선만 원한 것인지, 그것이 놓인 판을
/// 원한 것인지 모델은 알 수 없다. SAM 은 원래 크기가 다른 후보 네 개를 내놓고 그중 하나를 고르는데,
/// 고르기 직전 값을 <c>all_low_res_masks</c> 로 꺼내면 사람이 직접 고를 수 있다
/// (<c>scripts/patch_sam_decoder_multimask.py</c>). 그 출력이 없는 디코더면 후보 하나로 돈다.
/// </para>
/// <para><b>이전 마스크를 되먹인다.</b>
/// 두 번째 클릭부터는 직전 단계의 저해상도 마스크를 <c>mask_input</c> 으로 함께 넣는다.
/// 공식 SamPredictor 가 하는 것과 같고, 이게 없으면 배경 점을 찍어도 잘 듣지 않는다.
/// 화면은 상태를 갖지 않고 매번 점 전체를 보내므로, 서버가 '앞 점들'을 열쇠로 직전 마스크를 찾아 쓴다.
/// </para>
/// <para><b>임베딩 캐시.</b>
/// 무거운 쪽은 인코더다(1024×1024 한 번). 같은 사진을 여러 번 클릭하는 것이 라벨링의 기본 동작이라
/// 이미지 해시로 임베딩을 메모리에 들고 있는다. 디스크에는 두지 않는다 — 한 장에 4MB 라
/// 이미지 만 장이면 40GB 가 되고, 그만큼을 아끼자고 정리 정책을 하나 더 만들 이유가 없다.
/// </para>
/// </summary>
public sealed class SamAssistService : IDisposable
{
    /// <summary>패치한 디코더가 내주는 후보 마스크 출력 이름 (patch_sam_decoder_multimask.py 와 같아야 한다)</summary>
    private const string AllMasksOutput = "all_low_res_masks";
    private const string AllScoresOutput = "all_iou_predictions";

    /// <summary>SAM 저해상도 마스크 한 변</summary>
    private const int LowResSize = 256;

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
    private bool _multiMask;
    private volatile string? _loadFailure;
    private DateTimeOffset _loadFailedAt;

    private readonly object _cacheLock = new();
    private readonly Dictionary<string, CachedEmbedding> _cache = [];
    private readonly List<string> _cacheOrder = [];

    private readonly object _refineLock = new();
    private readonly Dictionary<string, RefineState> _refine = [];
    private readonly List<string> _refineOrder = [];

    private sealed record CachedEmbedding(float[] Data, int[] Shape, SamGeometry Geometry);

    /// <summary>되먹임에 쓸 직전 단계의 저해상도 마스크들 (후보 순서는 응답과 같다)</summary>
    private sealed record RefineState(float[][] LowRes, int Best);

    /// <summary>후보 하나가 갖는 것 — 저해상도 마스크와 점수. 폴리곤은 이 뒤에 만든다.</summary>
    private sealed record RawCandidate(float[] LowRes, double Score, bool ModelChoice);

    public SamAssistService(IOptions<SamOptions> options, ILogger<SamAssistService> logger,
        TimeProvider clock, IHostEnvironment environment)
    {
        _options = options.Value;
        _logger = logger;
        _clock = clock;
        _basePath = environment.ContentRootPath;
    }

    /// <summary>설정과 파일이 갖춰졌는지. 이게 false 면 화면에서 SAM 을 아예 숨긴다.</summary>
    public bool Available => Status().Available;

    /// <summary>후보를 여러 개 낼 수 있는 디코더인가 (모델을 올린 뒤에만 알 수 있다)</summary>
    public bool MultiMask => _multiMask;

    public (bool Available, bool Ready, string? Message) Status()
    {
        if (!_options.Enabled)
            return (false, false, "SAM 보조가 설정에서 꺼져 있습니다 (Sam:Enabled).");

        var encoder = _options.ResolvedEncoderPath(_basePath);
        if (encoder is null)
            return (false, false, "SAM 인코더 경로가 설정되지 않았습니다 (Sam:EncoderPath).");
        if (!File.Exists(encoder)) return (false, false, $"인코더 파일이 없습니다: {encoder}");

        if (_options.ResolvedDecoderPath(_basePath) is null)
            return (false, false, "SAM 디코더 파일이 없습니다 (Sam:DecoderPath · Sam:DecoderFallbackPath).");

        if (_loadFailure is { } failure) return (true, false, failure);
        return (true, _encoder is not null && _decoder is not null, null);
    }

    /// <summary>
    /// 모델만 미리 올려 둔다. 세션을 여는 데만 1초 남짓 걸려서, 첫 사용자가 그 값을 치르지 않도록
    /// 서버가 뜰 때 뒤에서 부른다. 실패해도 서버는 그대로 뜨고 상태에 이유가 남는다.
    /// </summary>
    public async Task WarmAsync(CancellationToken ct)
    {
        if (!Status().Available) return;
        try { await EnsureLoadedAsync(ct); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning("SAM 모델 예열 실패 — 첫 클릭 때 다시 시도합니다: {Message}", ex.Message);
        }
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

    /// <summary>클릭 점들로 마스크 후보를 만들고 폴리곤으로 접어 돌려준다.</summary>
    /// <param name="preferIndex">
    /// 화면이 지금 보고 있는 후보 자리. 되먹일 마스크를 고르는 데 쓴다.
    /// 없으면 직전 단계에서 모델이 고른 것을 쓴다.
    /// </param>
    public async Task<SamResult> PredictAsync(
        string cacheKey, Func<CancellationToken, Task<Stream>> open,
        IReadOnlyList<SamClick> clicks, int? preferIndex, CancellationToken ct)
    {
        if (clicks.Count == 0) return new SamResult([], -1, "클릭한 점이 없습니다.");

        var embedding = await GetEmbeddingAsync(cacheKey, open, ct);
        var decoder = _decoder ?? throw new InvalidOperationException("디코더가 준비되지 않았습니다.");

        // 직전 단계(마지막 점을 뺀 것)의 마스크가 있으면 되먹인다
        var previous = TryGetRefine(RefineKey(cacheKey, clicks.Take(clicks.Count - 1)));
        float[]? seedMask = null;
        if (previous is not null && previous.LowRes.Length > 0)
        {
            int index = preferIndex is { } wanted && wanted >= 0 && wanted < previous.LowRes.Length
                ? wanted
                : Math.Clamp(previous.Best, 0, previous.LowRes.Length - 1);
            seedMask = previous.LowRes[index];
        }

        await _decodeGate.WaitAsync(ct);
        try
        {
            var (result, state) = await Task.Run(
                () => Decode(decoder, embedding, clicks, seedMask, _multiMask), ct);
            if (state is not null) StoreRefine(RefineKey(cacheKey, clicks), state);
            return result;
        }
        finally { _decodeGate.Release(); }
    }

    // ───────────── 디코더 ─────────────

    private (SamResult Result, RefineState? State) Decode(
        InferenceSession decoder, CachedEmbedding embedding,
        IReadOnlyList<SamClick> clicks, float[]? seedMask, bool multiMask)
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

        // 두 번째 클릭부터는 직전 마스크를 함께 넣는다 — 배경 점이 제대로 듣게 하는 핵심이다
        var maskInput = new DenseTensor<float>([1, 1, LowResSize, LowResSize]);
        var hasMask = new DenseTensor<float>([1]);
        if (seedMask is { Length: LowResSize * LowResSize })
        {
            seedMask.CopyTo(maskInput.Buffer.Span);
            hasMask[0] = 1f;
        }

        var inputs = new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor("image_embeddings", new DenseTensor<float>(embedding.Data, embedding.Shape)),
            NamedOnnxValue.CreateFromTensor("point_coords", coords),
            NamedOnnxValue.CreateFromTensor("point_labels", labels),
            NamedOnnxValue.CreateFromTensor("mask_input", maskInput),
            NamedOnnxValue.CreateFromTensor("has_mask_input", hasMask),
            NamedOnnxValue.CreateFromTensor("orig_im_size", originalSize),
        };

        using var results = decoder.Run(inputs);

        var raw = multiMask ? ReadAllCandidates(results, count) : ReadSingleCandidate(results);
        if (raw.Count == 0)
            return (new SamResult([], -1, "디코더가 예상과 다른 모양을 돌려주었습니다."), null);

        // 클릭한 자리를 담은 덩어리를 고르게 한다 (배경 점은 씨앗이 아니다)
        var seeds = clicks.Where(c => c.Foreground).Select(c => new NormPoint(c.X, c.Y)).ToList();

        var built = new List<Built>();
        foreach (var candidate in raw)
        {
            var logits = MaskUpscaler.Expand(candidate.LowRes, LowResSize, geometry);
            var shape = MaskContour.FromLogits(logits, geometry.ScaledWidth, geometry.ScaledHeight, seeds);
            if (shape is null) continue;
            // 점이 흩뿌려진 후보는 사람에게 보여 줄 해석이 아니다. 모델이 고른 것은 예외로 남긴다 —
            // 그것마저 버리면 아무것도 못 주는 상황이 생긴다.
            if (shape.PartCount > MaskContour.MaxParts && !candidate.ModelChoice) continue;
            built.Add(new Built(new SamCandidate(shape, Math.Clamp(candidate.Score, 0, 1)),
                candidate.LowRes, candidate.ModelChoice));
        }

        if (built.Count == 0)
            return (new SamResult([], -1,
                "이 위치에서 객체를 찾지 못했습니다. 다른 곳을 클릭하거나 점을 더 찍어 보세요."), null);

        built = Deduplicate(built);
        // 작은 것부터 — 사람이 "더 크게 / 더 작게" 로 이해하기 쉽다
        built = [.. built.OrderBy(b => b.Candidate.Shape.PixelArea)];

        int best = built.FindIndex(b => b.ModelChoice);
        if (best < 0) best = 0;

        var state = new RefineState([.. built.Select(b => b.LowRes)], best);
        return (new SamResult([.. built.Select(b => b.Candidate)], best, null), state);
    }

    private sealed record Built(SamCandidate Candidate, float[] LowRes, bool ModelChoice);

    /// <summary>패치한 디코더에서 후보 전부를 읽는다.</summary>
    private static List<RawCandidate> ReadAllCandidates(
        IDisposableReadOnlyCollection<DisposableNamedOnnxValue> results, int pointCount)
    {
        var masks = results.FirstOrDefault(r => r.Name == AllMasksOutput)?.AsTensor<float>();
        var scores = results.FirstOrDefault(r => r.Name == AllScoresOutput)?.AsTensor<float>();
        if (masks is null || scores is null || masks.Dimensions.Length != 4) return [];

        int candidates = masks.Dimensions[1];
        int plane = masks.Dimensions[2] * masks.Dimensions[3];
        if (plane != LowResSize * LowResSize) return [];

        var flat = masks.ToArray();
        int choice = ModelChoice(scores, candidates, pointCount);

        var list = new List<RawCandidate>(candidates);
        for (int i = 0; i < candidates; i++)
        {
            var slice = new float[plane];
            Array.Copy(flat, i * plane, slice, 0, plane);
            list.Add(new RawCandidate(slice, scores[0, i], i == choice));
        }
        return list;
    }

    /// <summary>
    /// 모델이 스스로 고르는 규칙을 그대로 다시 센다 (공식 <c>select_masks</c>).
    /// 점이 하나면 0번을 크게 깎아 1~3번 중에서 고르고, 점이 여럿이면 0번이 이긴다.
    /// </summary>
    private static int ModelChoice(Tensor<float> scores, int candidates, int pointCount)
    {
        int best = 0;
        double bestScore = double.MinValue;
        for (int i = 0; i < candidates; i++)
        {
            double score = scores[0, i] + (pointCount - 2.5) * (i == 0 ? 1000 : 0);
            if (score > bestScore) { bestScore = score; best = i; }
        }
        return best;
    }

    /// <summary>패치하지 않은 디코더 — 모델이 고른 하나만 온다.</summary>
    private static List<RawCandidate> ReadSingleCandidate(
        IDisposableReadOnlyCollection<DisposableNamedOnnxValue> results)
    {
        var low = results.FirstOrDefault(r => r.Name == "low_res_masks")?.AsTensor<float>();
        if (low is null || low.Dimensions.Length != 4) return [];
        int plane = low.Dimensions[2] * low.Dimensions[3];
        if (plane != LowResSize * LowResSize) return [];

        double score = 0;
        var iou = results.FirstOrDefault(r => r.Name == "iou_predictions")?.AsTensor<float>();
        if (iou is not null && iou.Dimensions.Length == 2 && iou.Dimensions[1] > 0) score = iou[0, 0];

        var slice = new float[plane];
        Array.Copy(low.ToArray(), 0, slice, 0, plane);
        return [new RawCandidate(slice, score, true)];
    }

    /// <summary>
    /// 거의 같은 후보를 걷어낸다. SAM 의 네 후보 중 둘 이상이 사실상 같은 마스크인 경우가 흔해서,
    /// 그대로 두면 사용자가 후보를 넘겨도 화면이 안 바뀌는 것처럼 보인다.
    /// </summary>
    private static List<Built> Deduplicate(List<Built> items)
    {
        var kept = new List<Built>();
        foreach (var item in items.OrderByDescending(i => i.ModelChoice).ThenByDescending(i => i.Candidate.Score))
        {
            int same = kept.FindIndex(k => Similar(k.Candidate.Shape, item.Candidate.Shape));
            if (same < 0) kept.Add(item);
            // 모델이 고른 것은 반드시 남는다 — 중복이면 남아 있는 쪽에 그 표시를 옮긴다
            else if (item.ModelChoice) kept[same] = kept[same] with { ModelChoice = true };
        }
        return kept;
    }

    private static bool Similar(MaskShape a, MaskShape b)
    {
        double smaller = Math.Min(a.PixelArea, b.PixelArea);
        double larger = Math.Max(a.PixelArea, b.PixelArea);
        if (larger <= 0) return true;
        if (smaller / larger < 0.92) return false;
        return BoxIou(a.Box, b.Box) > 0.9;
    }

    private static double BoxIou(NormBox a, NormBox b)
    {
        double left = Math.Max(a.X, b.X), top = Math.Max(a.Y, b.Y);
        double right = Math.Min(a.X + a.Width, b.X + b.Width);
        double bottom = Math.Min(a.Y + a.Height, b.Y + b.Height);
        double overlap = Math.Max(0, right - left) * Math.Max(0, bottom - top);
        double union = a.Area + b.Area - overlap;
        return union <= 0 ? 1 : overlap / union;
    }

    // ───────────── 되먹임 캐시 ─────────────

    private static string RefineKey(string cacheKey, IEnumerable<SamClick> clicks) =>
        cacheKey + "|" + string.Join(';', clicks.Select(c => $"{c.X:F4},{c.Y:F4},{(c.Foreground ? 1 : 0)}"));

    private RefineState? TryGetRefine(string key)
    {
        lock (_refineLock)
        {
            if (!_refine.TryGetValue(key, out var value)) return null;
            _refineOrder.Remove(key);
            _refineOrder.Add(key);
            return value;
        }
    }

    private void StoreRefine(string key, RefineState state)
    {
        lock (_refineLock)
        {
            _refine[key] = state;
            _refineOrder.Remove(key);
            _refineOrder.Add(key);
            // 한 항목이 256×256 float 를 후보 수만큼 = 1MB 남짓. 진행 중인 몇 건만 들고 있으면 된다.
            while (_refineOrder.Count > Math.Max(4, _options.MaxRefineStates))
            {
                _refine.Remove(_refineOrder[0]);
                _refineOrder.RemoveAt(0);
            }
        }
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

                    // _decoder 를 마지막에 넣는다. 다른 요청은 이 필드로 "준비됐다" 를 판단하므로,
                    // 그보다 먼저 보이는 값은 모두 확정돼 있어야 한다.
                    _multiMask = decoder.OutputMetadata.ContainsKey(AllMasksOutput)
                        && decoder.OutputMetadata.ContainsKey(AllScoresOutput);
                    _encoder = encoder;
                    _decoder = decoder;
                }, ct);

                _loadFailure = null;
                _logger.LogInformation("SAM 모델 적재 완료 · 인코더 {Encoder} · 디코더 {Decoder} · 후보 {Mode}",
                    encoderPath, decoderPath, _multiMask ? "여러 개" : "하나 (단일 마스크 디코더)");
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
