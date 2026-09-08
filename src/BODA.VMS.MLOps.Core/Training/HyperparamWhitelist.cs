using System.Globalization;
using System.Text.Json;
using BODA.VMS.MLOps.Core.Domain;

namespace BODA.VMS.MLOps.Core.Training;

public enum HyperparamType { Int, Float, Bool, Choice }

/// <summary>화이트리스트 인자 정의 — 타입·범위·허용값. 스크립트 argparse 와 1:1 (scripts/train_*.py)</summary>
public sealed record HyperparamSpec(string Key, HyperparamType Type, double? Min = null, double? Max = null, string[]? Choices = null, string? Default = null)
{
    public string CliFlag => "--" + Key;
}

/// <summary>
/// 작업 생성 시(서버 400) 와 실행 시(워커) 두 곳에서 같은 규칙으로 하이퍼파라미터를 검증한다 (Phase 3 §5.2, §8).
/// 화이트리스트 밖 키·타입 불일치·범위 초과는 거부. 문자열 연결 없이 ArgumentList 로만 전달된다.
/// </summary>
public static class HyperparamWhitelist
{
    private static readonly HyperparamSpec[] Common =
    [
        new("epochs", HyperparamType.Int, 1, 10000, Default: "60"),
        new("lr", HyperparamType.Float, 1e-7, 10, Default: "0.001"),
        new("batch_size", HyperparamType.Int, 1, 1024, Default: "8"),
        new("imgsz", HyperparamType.Int, 32, 4096, Default: "640"),
    ];

    private static readonly HyperparamSpec[] Aug =
    [
        new("mosaic", HyperparamType.Float, 0, 1, Default: "0"),
        new("mixup", HyperparamType.Float, 0, 1, Default: "0"),
        new("hsv_h", HyperparamType.Float, 0, 1, Default: "0.015"),
        new("hsv_s", HyperparamType.Float, 0, 1, Default: "0.7"),
        new("hsv_v", HyperparamType.Float, 0, 1, Default: "0.4"),
    ];

    private static readonly Dictionary<TrainingScript, HyperparamSpec[]> PerScript = new()
    {
        [TrainingScript.TrainDfine] =
        [
            .. Common, .. Aug,
            new("flip", HyperparamType.Float, 0, 1, Default: "0.5"),
            new("weight_decay", HyperparamType.Float, 0, 1, Default: "0.000125"),
            new("warmup_epochs", HyperparamType.Float, 0, 100, Default: "1.0"),
            new("workers", HyperparamType.Int, 0, 32, Default: "0"),
        ],
        [TrainingScript.TrainYolo] = [.. Common, .. Aug],
        [TrainingScript.TrainClassifier] = Common,
        [TrainingScript.TrainAnomaly] =
        [
            .. Common,
            new("method", HyperparamType.Choice, Choices: ["patchcore", "fastflow", "efficient_ad"], Default: "patchcore"),
            new("backbone", HyperparamType.Choice, Choices: ["resnet18", "resnet50", "wide_resnet50_2"], Default: "resnet18"),
            new("coreset_ratio", HyperparamType.Float, 0.01, 1, Default: "0.1"),
        ],
        [TrainingScript.TrainPpocr] =
        [
            new("epochs", HyperparamType.Int, 1, 10000, Default: "20"),
            new("lr", HyperparamType.Float, 1e-7, 10, Default: "0.001"),
            new("batch_size", HyperparamType.Int, 1, 1024, Default: "8"),
            new("target", HyperparamType.Choice, Choices: ["detection", "recognition"], Default: "recognition"),
        ],
        [TrainingScript.TrainRfdetrSeg] = [.. Common, .. Aug],
    };

    public static IReadOnlyList<HyperparamSpec> For(TrainingScript script) => PerScript[script];

    public static HyperparamSpec? Find(TrainingScript script, string key) =>
        PerScript[script].FirstOrDefault(s => s.Key.Equals(key, StringComparison.Ordinal));

    /// <summary>
    /// JSON 객체를 검증·정규화한다. 반환은 키→인자 문자열(InvariantCulture). 오류가 있으면 errors 에 채우고 null.
    /// </summary>
    public static Dictionary<string, string>? Validate(TrainingScript script, JsonElement? hyperparams, out List<string> errors)
    {
        errors = new List<string>();
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (hyperparams is null || hyperparams.Value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            return result;
        if (hyperparams.Value.ValueKind != JsonValueKind.Object)
        {
            errors.Add("hyperparams 는 JSON 객체여야 합니다.");
            return null;
        }

        foreach (var prop in hyperparams.Value.EnumerateObject())
        {
            var spec = Find(script, prop.Name);
            if (spec is null) { errors.Add($"허용되지 않은 하이퍼파라미터: {prop.Name}"); continue; }
            var normalized = Normalize(spec, prop.Value, out var err);
            if (normalized is null) errors.Add($"{prop.Name}: {err}");
            else result[spec.Key] = normalized;
        }
        return errors.Count == 0 ? result : null;
    }

    /// <summary>이미 정규화된 문자열 사전(워커가 서버에서 받은 것)을 재검증한다 — 서버·워커 이중 게이트</summary>
    public static bool ValidateNormalized(TrainingScript script, IReadOnlyDictionary<string, string> hyperparams, out List<string> errors)
    {
        errors = new List<string>();
        foreach (var (key, value) in hyperparams)
        {
            var spec = Find(script, key);
            if (spec is null) { errors.Add($"허용되지 않은 하이퍼파라미터: {key}"); continue; }
            using var doc = JsonDocument.Parse(spec.Type == HyperparamType.Choice ? JsonSerializer.Serialize(value) : value);
            if (Normalize(spec, doc.RootElement, out var err) is null) errors.Add($"{key}: {err}");
        }
        return errors.Count == 0;
    }

    public static string? Normalize(HyperparamSpec spec, JsonElement value, out string? error)
    {
        error = null;
        var inv = CultureInfo.InvariantCulture;
        switch (spec.Type)
        {
            case HyperparamType.Int:
            {
                long v;
                if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out v)) { }
                else if (value.ValueKind == JsonValueKind.String && long.TryParse(value.GetString(), NumberStyles.Integer, inv, out v)) { }
                else { error = "정수여야 합니다."; return null; }
                if ((spec.Min is not null && v < spec.Min) || (spec.Max is not null && v > spec.Max)) { error = $"범위 [{spec.Min}, {spec.Max}] 를 벗어났습니다."; return null; }
                return v.ToString(inv);
            }
            case HyperparamType.Float:
            {
                double v;
                if (value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out v)) { }
                else if (value.ValueKind == JsonValueKind.String && double.TryParse(value.GetString(), NumberStyles.Float, inv, out v)) { }
                else { error = "숫자여야 합니다."; return null; }
                if (double.IsNaN(v) || double.IsInfinity(v)) { error = "유한한 숫자여야 합니다."; return null; }
                if ((spec.Min is not null && v < spec.Min) || (spec.Max is not null && v > spec.Max)) { error = $"범위 [{spec.Min}, {spec.Max}] 를 벗어났습니다."; return null; }
                return v.ToString("R", inv);
            }
            case HyperparamType.Bool:
                if (value.ValueKind is JsonValueKind.True or JsonValueKind.False) return value.GetBoolean() ? "true" : "false";
                error = "불리언이어야 합니다."; return null;
            case HyperparamType.Choice:
            {
                var s = value.ValueKind == JsonValueKind.String ? value.GetString() : null;
                if (s is null || spec.Choices is null || !spec.Choices.Contains(s, StringComparer.Ordinal))
                { error = $"허용값: {string.Join("|", spec.Choices ?? [])}"; return null; }
                return s;
            }
            default:
                error = "알 수 없는 타입"; return null;
        }
    }
}
