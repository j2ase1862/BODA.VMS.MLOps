using System.Text.Json;
using System.Text.Json.Serialization;
using BODA.VMS.MLOps.Core.Domain;

namespace BODA.VMS.MLOps.Core.Labeling;

/// <summary>0~1 로 정규화된 좌표. 원본 해상도가 달라도 라벨이 그대로 유효하다.</summary>
public readonly record struct NormPoint(double X, double Y);

/// <summary>0~1 정규화 사각형. 좌상단 기준.</summary>
public readonly record struct NormBox(double X, double Y, double Width, double Height)
{
    public double CenterX => X + Width / 2;
    public double CenterY => Y + Height / 2;
    public double Area => Width * Height;

    public NormBox Clamped()
    {
        var x = Math.Clamp(X, 0, 1);
        var y = Math.Clamp(Y, 0, 1);
        return new NormBox(x, y, Math.Clamp(Width, 0, 1 - x), Math.Clamp(Height, 0, 1 - y));
    }
}

/// <summary>
/// 라벨 하나. 도형에 따라 채워지는 필드가 다르다 (개발 문서 §4 Annotation).
/// 좌표는 항상 정규화 값이고, 저장은 <see cref="ToJson"/> 의 payload 로 한다.
/// </summary>
public sealed record LabelAnnotation
{
    public required AnnotationShape Shape { get; init; }
    public required string ClassName { get; init; }
    /// <summary>Box·Text 의 사각형</summary>
    public NormBox? Box { get; init; }
    /// <summary>Polygon 의 꼭짓점 (3개 이상)</summary>
    public IReadOnlyList<NormPoint>? Polygon { get; init; }
    /// <summary>Text 도형이 담는 인식 문자열</summary>
    public string? Text { get; init; }

    /// <summary>
    /// 학습에 쓰는 외접 박스. 폴리곤은 여기서 박스로 접힌다
    /// (검출 스크립트가 폴리곤을 외접 박스로 다루는 기존 동작과 같다).
    /// </summary>
    public NormBox? BoundingBox()
    {
        if (Box is { } box) return box;
        if (Polygon is not { Count: > 0 } points) return null;

        double minX = points.Min(p => p.X), maxX = points.Max(p => p.X);
        double minY = points.Min(p => p.Y), maxY = points.Max(p => p.Y);
        return new NormBox(minX, minY, maxX - minX, maxY - minY).Clamped();
    }

    /// <summary>도형에 필요한 값이 다 있고 범위 안인지. 화면과 서버가 같은 규칙을 쓴다.</summary>
    public bool IsValid(out string? error)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(ClassName)) { error = "클래스가 비어 있습니다."; return false; }

        switch (Shape)
        {
            case AnnotationShape.Classification:
                return true;

            case AnnotationShape.Box:
            case AnnotationShape.Text:
                if (Box is not { } b) { error = $"{Shape} 는 사각형이 필요합니다."; return false; }
                if (!InRange(b.X) || !InRange(b.Y) || !InRange(b.Width) || !InRange(b.Height))
                { error = "사각형 좌표가 0~1 범위를 벗어났습니다."; return false; }
                if (b.Width <= 0 || b.Height <= 0) { error = "사각형의 너비와 높이는 0보다 커야 합니다."; return false; }
                if (b.X + b.Width > 1.0001 || b.Y + b.Height > 1.0001) { error = "사각형이 이미지 밖으로 나갑니다."; return false; }
                if (Shape == AnnotationShape.Text && string.IsNullOrEmpty(Text)) { error = "OCR 라벨은 텍스트가 필요합니다."; return false; }
                return true;

            case AnnotationShape.Polygon:
                if (Polygon is not { Count: >= 3 }) { error = "폴리곤은 꼭짓점이 3개 이상이어야 합니다."; return false; }
                if (Polygon.Any(p => !InRange(p.X) || !InRange(p.Y)))
                { error = "폴리곤 좌표가 0~1 범위를 벗어났습니다."; return false; }
                return true;

            default:
                error = $"알 수 없는 도형: {Shape}";
                return false;
        }
    }

    private static bool InRange(double v) => v is >= -0.0001 and <= 1.0001 && !double.IsNaN(v);
}

/// <summary>저장·전송용 payload. 도형별로 쓰는 필드만 채워 보낸다.</summary>
public sealed class AnnotationPayload
{
    public double? X { get; set; }
    public double? Y { get; set; }
    public double? W { get; set; }
    public double? H { get; set; }
    public double[][]? Points { get; set; }
    public string? Text { get; set; }

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        // OCR 라벨 텍스트가 한글일 수 있다. 이스케이프하면 DB 에 저장된 값이 원문과 달라진다.
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static string ToJson(LabelAnnotation a)
    {
        var p = new AnnotationPayload { Text = a.Text };
        if (a.Box is { } b) { p.X = b.X; p.Y = b.Y; p.W = b.Width; p.H = b.Height; }
        if (a.Polygon is { Count: > 0 } poly) p.Points = poly.Select(pt => new[] { pt.X, pt.Y }).ToArray();
        return JsonSerializer.Serialize(p, Json);
    }

    /// <summary>저장된 payload 를 되읽는다. 깨진 값이면 null (호출 측이 건너뛴다).</summary>
    public static LabelAnnotation? FromJson(AnnotationShape shape, string className, string? payloadJson)
    {
        AnnotationPayload? p = null;
        if (!string.IsNullOrWhiteSpace(payloadJson))
        {
            try { p = JsonSerializer.Deserialize<AnnotationPayload>(payloadJson, Json); }
            catch (JsonException) { return null; }
        }
        p ??= new AnnotationPayload();

        NormBox? box = p is { X: not null, Y: not null, W: not null, H: not null }
            ? new NormBox(p.X.Value, p.Y.Value, p.W.Value, p.H.Value)
            : null;
        var polygon = p.Points?.Where(pt => pt.Length >= 2).Select(pt => new NormPoint(pt[0], pt[1])).ToList();

        return new LabelAnnotation
        {
            Shape = shape,
            ClassName = className,
            Box = box,
            Polygon = polygon is { Count: > 0 } ? polygon : null,
            Text = p.Text,
        };
    }
}
