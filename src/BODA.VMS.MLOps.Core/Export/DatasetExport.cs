using System.Globalization;
using System.Text;
using System.Text.Json;
using BODA.VMS.MLOps.Core.Domain;
using BODA.VMS.MLOps.Core.Labeling;

namespace BODA.VMS.MLOps.Core.Export;

/// <summary>내보내기에 들어가는 이미지 한 장</summary>
public sealed record ExportImage(
    Guid ImageId,
    string Sha256,
    string FileName,
    DatasetSplit Split,
    int Width,
    int Height,
    IReadOnlyList<LabelAnnotation> Annotations);

/// <summary>내보내기 한 벌의 입력. 스냅샷이 이 모양으로 굳는다.</summary>
public sealed record ExportManifest(
    string DatasetName,
    TaskType TaskType,
    IReadOnlyList<string> Classes,
    IReadOnlyList<ExportImage> Images);

/// <summary>
/// 내보내기 대상. zip 이든 폴더든 이 인터페이스만 구현하면 되고,
/// 덕분에 형식 규칙을 파일 시스템 없이 시험할 수 있다.
/// </summary>
public interface IExportSink
{
    void WriteText(string path, string content);
    /// <summary>원본 이미지 바이트를 그 경로로 복사한다 (재인코딩하지 않는다 — 검사 재현성)</summary>
    void WriteImage(string path, Guid imageId);
}

/// <summary>
/// 데이터셋 스냅샷을 학습 스크립트가 그대로 읽는 구조로 쓴다.
/// 구조는 scripts/train_*.py 가 기대하는 것과 1:1 이어야 하므로, 스크립트를 바꾸면 여기도 함께 바꾼다.
///
/// <list type="bullet">
/// <item>yolo        — data.yaml + images/{train,val}/ + labels/{train,val}/*.txt (정규화 cx cy w h)</item>
/// <item>imagefolder — {train,val}/{class}/*</item>
/// <item>mvtec       — train/good/ + test/good/ + test/{class}/</item>
/// <item>ppocr       — {train,val}_crops/ + {train,val}_rec.txt (경로 TAB 라벨) + dict.txt</item>
/// <item>coco        — images/{split}/ + annotations/instances_{split}.json</item>
/// </list>
/// </summary>
public static class DatasetExportWriter
{
    /// <summary>정상 클래스로 간주하는 이름 (이상탐지). MVTec 은 정상을 good 폴더에 둔다.</summary>
    public static readonly string[] NormalClassNames = ["good", "normal", "ok", "정상"];

    public static void Write(ExportManifest manifest, string format, IExportSink sink)
    {
        switch (format.ToLowerInvariant())
        {
            case "yolo": WriteYolo(manifest, sink); break;
            case "imagefolder": WriteImageFolder(manifest, sink); break;
            case "mvtec": WriteMvtec(manifest, sink); break;
            case "ppocr": WritePpOcr(manifest, sink); break;
            case "coco": WriteCoco(manifest, sink); break;
            default: throw new ArgumentException($"알 수 없는 내보내기 형식: {format}", nameof(format));
        }
    }

    /// <summary>작업 유형이 쓰는 기본 형식</summary>
    public static string DefaultFormat(TaskType taskType) => taskType switch
    {
        TaskType.Detection => "yolo",
        TaskType.Classification => "imagefolder",
        TaskType.Anomaly => "mvtec",
        TaskType.Ocr => "ppocr",
        TaskType.Segmentation => "coco",
        _ => throw new ArgumentOutOfRangeException(nameof(taskType)),
    };

    // ───────────── YOLO (검출) ─────────────

    private static void WriteYolo(ExportManifest m, IExportSink sink)
    {
        var index = ClassIndex(m.Classes);
        var yaml = new StringBuilder()
            .Append("path: .\n")
            .Append("train: images/train\n")
            .Append("val: images/val\n")
            .Append("names:\n");
        for (int i = 0; i < m.Classes.Count; i++) yaml.Append($"  {i}: {m.Classes[i]}\n");
        sink.WriteText("data.yaml", yaml.ToString());

        foreach (var img in m.Images)
        {
            var split = SplitDir(img.Split);
            sink.WriteImage($"images/{split}/{img.FileName}", img.ImageId);

            var lines = new StringBuilder();
            foreach (var a in img.Annotations)
            {
                if (!index.TryGetValue(a.ClassName, out int classId)) continue;
                if (a.BoundingBox() is not { } box) continue;
                var b = box.Clamped();
                if (b.Width <= 0 || b.Height <= 0) continue;
                lines.Append(string.Join(' ',
                    classId.ToString(CultureInfo.InvariantCulture),
                    F(b.CenterX), F(b.CenterY), F(b.Width), F(b.Height))).Append('\n');
            }
            // 라벨이 없어도 파일은 만든다 — 배경 전용 이미지(negative sample)로 학습에 쓰인다
            sink.WriteText($"labels/{split}/{Path.GetFileNameWithoutExtension(img.FileName)}.txt", lines.ToString());
        }
    }

    // ───────────── ImageFolder (분류) ─────────────

    private static void WriteImageFolder(ExportManifest m, IExportSink sink)
    {
        foreach (var img in m.Images)
        {
            var cls = img.Annotations.FirstOrDefault(a => a.Shape == AnnotationShape.Classification)?.ClassName;
            if (cls is null) continue; // 라벨 없는 이미지는 분류 학습에 넣을 자리가 없다
            sink.WriteImage($"{SplitDir(img.Split)}/{SafeSegment(cls)}/{img.FileName}", img.ImageId);
        }
    }

    // ───────────── MVTec (이상탐지) ─────────────

    private static void WriteMvtec(ExportManifest m, IExportSink sink)
    {
        // 정상은 train/good 과 test/good 으로, 이상은 test/{class} 로 간다.
        // 스크립트가 train/good 만 보고 학습하므로 이상 샘플은 평가에만 쓰인다.
        foreach (var img in m.Images)
        {
            var cls = img.Annotations.FirstOrDefault(a => a.Shape == AnnotationShape.Classification)?.ClassName;
            if (cls is null) continue;

            bool normal = IsNormalClass(cls);
            var path = (normal, img.Split) switch
            {
                (true, DatasetSplit.Train) => $"train/good/{img.FileName}",
                (true, _) => $"test/good/{img.FileName}",
                (false, _) => $"test/{SafeSegment(cls)}/{img.FileName}",
            };
            sink.WriteImage(path, img.ImageId);
        }
    }

    public static bool IsNormalClass(string className) =>
        NormalClassNames.Contains(className.Trim(), StringComparer.OrdinalIgnoreCase);

    // ───────────── PP-OCR (인식) ─────────────

    private static void WritePpOcr(ExportManifest m, IExportSink sink)
    {
        // 크롭을 만들지 않고 원본과 함께 라벨 텍스트를 낸다. 크롭은 서버가 잘라 넣는다
        // (여기서는 경로만 정하고, ExportImage.FileName 이 이미 크롭 파일명이다).
        var lines = new Dictionary<DatasetSplit, StringBuilder>();
        var chars = new SortedSet<char>();

        foreach (var img in m.Images)
        {
            var text = img.Annotations.FirstOrDefault(a => a.Shape == AnnotationShape.Text)?.Text;
            if (string.IsNullOrEmpty(text)) continue;

            var split = img.Split == DatasetSplit.Train ? DatasetSplit.Train : DatasetSplit.Val;
            var dir = $"{SplitDir(split)}_crops";
            sink.WriteImage($"{dir}/{img.FileName}", img.ImageId);

            if (!lines.TryGetValue(split, out var sb)) lines[split] = sb = new StringBuilder();
            sb.Append($"{dir}/{img.FileName}\t{text}\n");
            foreach (var c in text) chars.Add(c);
        }

        foreach (var split in new[] { DatasetSplit.Train, DatasetSplit.Val })
            sink.WriteText($"{SplitDir(split)}_rec.txt", lines.TryGetValue(split, out var sb) ? sb.ToString() : "");

        var dict = string.Join('\n', chars.Where(c => !char.IsControl(c)));
        sink.WriteText("dict.txt", dict);
        sink.WriteText("ppocr_keys_v1.txt", dict);
    }

    // ───────────── COCO (세그멘테이션) ─────────────

    private static void WriteCoco(ExportManifest m, IExportSink sink)
    {
        var index = ClassIndex(m.Classes);
        foreach (var split in new[] { DatasetSplit.Train, DatasetSplit.Val, DatasetSplit.Test })
        {
            var images = m.Images.Where(i => i.Split == split).ToList();
            if (images.Count == 0 && split == DatasetSplit.Test) continue;

            var cocoImages = new List<object>();
            var cocoAnnotations = new List<object>();
            int imageId = 1, annId = 1;

            foreach (var img in images)
            {
                sink.WriteImage($"images/{SplitDir(split)}/{img.FileName}", img.ImageId);
                cocoImages.Add(new { id = imageId, file_name = img.FileName, width = img.Width, height = img.Height });

                foreach (var a in img.Annotations)
                {
                    if (!index.TryGetValue(a.ClassName, out int classId)) continue;
                    if (a.BoundingBox() is not { } bb) continue;
                    var b = bb.Clamped();

                    // COCO 는 픽셀 좌표를 쓴다. 폴리곤이 없으면 박스 네 꼭짓점을 폴리곤으로 낸다.
                    double left = b.X * img.Width, top = b.Y * img.Height;
                    double right = (b.X + b.Width) * img.Width, bottom = (b.Y + b.Height) * img.Height;
                    var segmentation = a.Polygon is { Count: >= 3 } poly
                        ? new List<double[]> { poly.SelectMany(p => new[] { p.X * img.Width, p.Y * img.Height }).ToArray() }
                        : new List<double[]> { new[] { left, top, right, top, right, bottom, left, bottom } };

                    cocoAnnotations.Add(new
                    {
                        id = annId++,
                        image_id = imageId,
                        category_id = classId + 1, // COCO 카테고리는 1부터
                        bbox = new[] { b.X * img.Width, b.Y * img.Height, b.Width * img.Width, b.Height * img.Height },
                        area = b.Width * img.Width * b.Height * img.Height,
                        iscrowd = 0,
                        segmentation,
                    });
                }
                imageId++;
            }

            var categories = m.Classes.Select((name, i) => new { id = i + 1, name, supercategory = "none" }).ToList();
            var json = JsonSerializer.Serialize(new { images = cocoImages, annotations = cocoAnnotations, categories },
                new JsonSerializerOptions { WriteIndented = false });
            sink.WriteText($"annotations/instances_{SplitDir(split)}.json", json);
        }
    }

    // ───────────── 공용 ─────────────

    private static Dictionary<string, int> ClassIndex(IReadOnlyList<string> classes) =>
        classes.Select((c, i) => (c, i)).ToDictionary(t => t.c, t => t.i, StringComparer.Ordinal);

    public static string SplitDir(DatasetSplit split) => split switch
    {
        DatasetSplit.Train => "train",
        DatasetSplit.Val => "val",
        _ => "test",
    };

    /// <summary>클래스 이름을 폴더명으로 쓸 때 경로 구분자·상위 이동을 막는다</summary>
    public static string SafeSegment(string name)
    {
        var cleaned = new string(name.Trim().Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c).ToArray());
        cleaned = cleaned.Trim('.', ' ');
        return cleaned.Length == 0 ? "_" : cleaned;
    }

    private static string F(double v) => v.ToString("0.######", CultureInfo.InvariantCulture);
}
