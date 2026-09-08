using System.Text.Json;
using System.Text.RegularExpressions;
using BODA.VMS.MLOps.Core.Domain;
using VMS.Core.DeepLearning;

namespace BODA.VMS.MLOps.Core.Onnx;

/// <summary>업로드 검증 파이프라인(Phase 1 §4)이 쓰는 ONNX 검사 결과</summary>
public sealed class OnnxInspection
{
    public required bool IsValidProtobuf { get; init; }
    public required ModelFormat Format { get; init; }
    public required IReadOnlyDictionary<string, string> Metadata { get; init; }
    public required IReadOnlyList<OnnxTensorInfo> Inputs { get; init; }
    public required IReadOnlyList<OnnxTensorInfo> Outputs { get; init; }
    /// <summary>metadata_props 'names' 에서 파싱한 클래스 (없으면 null)</summary>
    public string[]? Classes { get; init; }
    /// <summary>metadata_props 'imgsz' 또는 입력 형상에서 얻은 정사각 입력 크기 (없으면 null)</summary>
    public int? InputSize { get; init; }
    public List<string> Warnings { get; } = new();
}

/// <summary>
/// 규약 판별 확장 — 판별 규칙은 VMS.Core.Contracts 의 <see cref="DetectionModelFormatProbe"/>(yolo/dfine)와 같고,
/// yoloseg / classifier / anomaly / ppocr 판별과 names·imgsz 메타 파싱을 더한다. 세션은 만들지 않는다.
/// 순서: metadata_props 'model_format' → 그래프 입출력 이름·형상 → Unknown(등록 허용, 경고).
///
/// <para>
/// 파일 읽기는 <see cref="OnnxSafeReader"/> 만 쓴다. 같은 패키지의 <c>OnnxMetadataReader</c>·
/// <c>DetectionModelFormatProbe.Probe(path)</c> 는 길이 필드를 남은 바이트와 대조하지 않아
/// 조작된 varint 하나로 파싱이 무한 반복될 수 있다. 여기 들어오는 파일은 업로드된 것이라 신뢰할 수 없으므로
/// 경로를 받는 그 API 들은 호출하지 않고, 순수 함수인 <see cref="DetectionModelFormatProbe.IsDFineLayout"/> 만 쓴다.
/// (VMS 리포의 해당 파서도 같은 보강이 필요하다.)
/// </para>
/// </summary>
public static class OnnxModelInspector
{
    public static OnnxInspection Inspect(string path)
    {
        var file = OnnxSafeReader.Read(path);
        var meta = file.Metadata;
        var inputs = file.Inputs;
        var outputs = file.Outputs;
        bool valid = outputs.Count > 0 || inputs.Count > 0;

        var format = ProbeFormat(meta, inputs, outputs);
        var classes = meta.TryGetValue("names", out var names) ? ParseNames(names) : null;
        int? inputSize = meta.TryGetValue("imgsz", out var imgsz) ? ParseImgsz(imgsz) : null;
        inputSize ??= InferInputSize(inputs);

        var result = new OnnxInspection
        {
            IsValidProtobuf = valid,
            Format = format,
            Metadata = meta,
            Inputs = inputs,
            Outputs = outputs,
            Classes = classes,
            InputSize = inputSize,
        };
        if (!valid) result.Warnings.Add("ONNX 그래프 입출력을 읽지 못했습니다 (protobuf 구조 손상 가능).");
        if (format == ModelFormat.Unknown) result.Warnings.Add("모델 규약을 판별하지 못했습니다 (unknown 으로 등록).");
        if (format == ModelFormat.Yolo) result.Warnings.Add("Ultralytics YOLO 파생 모델 — Enterprise License 확인이 필요합니다.");
        if (classes is null) result.Warnings.Add("metadata_props 'names' 가 없습니다. 클래스 목록을 입력하세요.");
        return result;
    }

    public static ModelFormat ProbeFormat(IReadOnlyDictionary<string, string> meta,
        IReadOnlyList<OnnxTensorInfo> inputs, IReadOnlyList<OnnxTensorInfo> outputs)
    {
        if (meta.TryGetValue(DetectionModelFormatProbe.MetadataKey, out var fmt))
        {
            switch (fmt.Trim().ToLowerInvariant())
            {
                case "dfine": case "rtdetr": return ModelFormat.DFine;
                case "yolo": return ModelFormat.Yolo;
                case "yoloseg": case "yolo-seg": case "yolo_seg": return ModelFormat.YoloSeg;
                case "classifier": case "classification": return ModelFormat.Classifier;
                case "anomaly": case "patchcore": case "anomalib": return ModelFormat.Anomaly;
                case "ppocr": case "paddleocr": return ModelFormat.PpOcr;
            }
        }

        var inNames = inputs.Select(i => i.Name).ToList();
        var outNames = outputs.Select(o => o.Name).ToList();
        if (DetectionModelFormatProbe.IsDFineLayout(inNames, outNames)) return ModelFormat.DFine;

        // yoloseg: output0 [N,4+nc+32,A] + output1 [N,32,mh,mw]
        if (outputs.Count == 2 && outputs.Any(o => o.Name == "output1" && o.Rank == 4)) return ModelFormat.YoloSeg;

        if (outputs.Count == 1)
        {
            var o = outputs[0];
            // yolo: 단일 3D 출력 [N,4+nc,A] / [N,A,4+nc]
            if (o.Rank == 3 && (o.Name.StartsWith("output", StringComparison.OrdinalIgnoreCase) || inNames.Contains("images")))
                return ModelFormat.Yolo;
            // classifier: 단일 2D [N,nc]
            if (o.Rank == 2) return ModelFormat.Classifier;
            // anomalib 내보내기: 입력 1개 + 단일 4D anomaly map
            if (o.Rank == 4 && inNames.Count == 1) return ModelFormat.Anomaly;
        }
        return ModelFormat.Unknown;
    }

    private static readonly Regex DictEntry = new(@"(\d+)\s*:\s*(?:'((?:[^'\\]|\\.)*)'|""((?:[^""\\]|\\.)*)"")", RegexOptions.Compiled);

    /// <summary>names 값 파싱 — Ultralytics/train_dfine 의 파이썬 dict repr <c>{0: 'good', 1: 'defect'}</c>, JSON 배열/객체, 콤마 구분 모두 허용</summary>
    public static string[]? ParseNames(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        raw = raw.Trim();
        try
        {
            if (raw.StartsWith('['))
                return JsonSerializer.Deserialize<string[]>(raw);
            if (raw.StartsWith('{') && raw.Contains('"'))
            {
                var dict = JsonSerializer.Deserialize<Dictionary<string, string>>(raw);
                if (dict is not null)
                    return dict.OrderBy(kv => int.TryParse(kv.Key, out var i) ? i : int.MaxValue).Select(kv => kv.Value).ToArray();
            }
        }
        catch (JsonException) { /* 파이썬 repr 로 폴백 */ }

        var matches = DictEntry.Matches(raw);
        if (matches.Count > 0)
            return matches.Cast<Match>()
                .Select(m => (Idx: int.Parse(m.Groups[1].Value), Name: m.Groups[2].Success ? m.Groups[2].Value : m.Groups[3].Value))
                .OrderBy(t => t.Idx).Select(t => t.Name).ToArray();

        var parts = raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return parts.Length > 0 ? parts : null;
    }

    /// <summary>imgsz 값 파싱 — <c>[640, 640]</c>, <c>640</c>, <c>(640, 640)</c></summary>
    public static int? ParseImgsz(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var m = Regex.Match(raw, @"\d+");
        return m.Success && int.TryParse(m.Value, out var v) && v > 0 ? v : null;
    }

    private static int? InferInputSize(IReadOnlyList<OnnxTensorInfo> inputs)
    {
        var img = inputs.FirstOrDefault(i => i.Rank == 4);
        if (img is null) return null;
        var h = img.Dims[2]; var w = img.Dims[3];
        return h is > 0 && h == w ? (int)h.Value : null;
    }
}
