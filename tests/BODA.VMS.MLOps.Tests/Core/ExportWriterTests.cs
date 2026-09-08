using BODA.VMS.MLOps.Core.Domain;
using BODA.VMS.MLOps.Core.Export;
using BODA.VMS.MLOps.Core.Hashing;
using BODA.VMS.MLOps.Core.Labeling;
using FluentAssertions;

namespace BODA.VMS.MLOps.Tests.Core;

/// <summary>
/// 내보내기 구조는 scripts/train_*.py 가 읽는 것과 정확히 같아야 한다.
/// 여기가 깨지면 학습이 데이터를 못 찾거나 클래스가 뒤바뀐다.
/// </summary>
public class ExportWriterTests
{
    private sealed class MemorySink : IExportSink
    {
        public Dictionary<string, string> Texts { get; } = new(StringComparer.Ordinal);
        public List<(string Path, Guid ImageId)> Images { get; } = [];
        public void WriteText(string path, string content) => Texts[path] = content;
        public void WriteImage(string path, Guid imageId) => Images.Add((path, imageId));
    }

    private static LabelAnnotation Box(string cls, double x, double y, double w, double h) =>
        new() { Shape = AnnotationShape.Box, ClassName = cls, Box = new NormBox(x, y, w, h) };

    private static LabelAnnotation Cls(string cls) =>
        new() { Shape = AnnotationShape.Classification, ClassName = cls };

    private static ExportImage Img(string name, DatasetSplit split, params LabelAnnotation[] labels) =>
        new(Guid.NewGuid(), new string('a', 64), name, split, 800, 600, labels);

    [Fact]
    public void Yolo_writes_data_yaml_images_and_normalized_labels()
    {
        var manifest = new ExportManifest("라인A", TaskType.Detection, ["good", "defect"], [
            Img("a.jpg", DatasetSplit.Train, Box("defect", 0.1, 0.2, 0.4, 0.2)),
            Img("b.jpg", DatasetSplit.Val, Box("good", 0, 0, 1, 1)),
        ]);
        var sink = new MemorySink();
        DatasetExportWriter.Write(manifest, "yolo", sink);

        sink.Texts["data.yaml"].Should().Contain("train: images/train").And.Contain("0: good").And.Contain("1: defect");
        sink.Images.Select(i => i.Path).Should().BeEquivalentTo(["images/train/a.jpg", "images/val/b.jpg"]);

        // class cx cy w h — defect 는 인덱스 1, 중심은 (0.3, 0.3)
        sink.Texts["labels/train/a.txt"].Trim().Should().Be("1 0.3 0.3 0.4 0.2");
        sink.Texts["labels/val/b.txt"].Trim().Should().Be("0 0.5 0.5 1 1");
    }

    [Fact]
    public void Yolo_writes_empty_label_file_for_background_images()
    {
        var manifest = new ExportManifest("bg", TaskType.Detection, ["defect"], [Img("empty.jpg", DatasetSplit.Train)]);
        var sink = new MemorySink();
        DatasetExportWriter.Write(manifest, "yolo", sink);

        // 라벨이 없어도 파일은 있어야 한다 — 배경 전용 샘플로 학습에 쓰인다
        sink.Texts.Should().ContainKey("labels/train/empty.txt");
        sink.Texts["labels/train/empty.txt"].Should().BeEmpty();
    }

    [Fact]
    public void Yolo_folds_polygon_into_bounding_box()
    {
        var polygon = new LabelAnnotation
        {
            Shape = AnnotationShape.Polygon,
            ClassName = "defect",
            Polygon = [new NormPoint(0.2, 0.3), new NormPoint(0.6, 0.3), new NormPoint(0.6, 0.7), new NormPoint(0.2, 0.7)],
        };
        var sink = new MemorySink();
        DatasetExportWriter.Write(new ExportManifest("p", TaskType.Detection, ["defect"],
            [Img("p.jpg", DatasetSplit.Train, polygon)]), "yolo", sink);

        // 중심 (0.4, 0.5), 크기 (0.4, 0.4)
        sink.Texts["labels/train/p.txt"].Trim().Should().Be("0 0.4 0.5 0.4 0.4");
    }

    [Fact]
    public void Yolo_skips_classes_not_in_the_dataset()
    {
        var sink = new MemorySink();
        DatasetExportWriter.Write(new ExportManifest("x", TaskType.Detection, ["good"],
            [Img("a.jpg", DatasetSplit.Train, Box("good", 0.1, 0.1, 0.2, 0.2), Box("사라진클래스", 0.5, 0.5, 0.1, 0.1))]),
            "yolo", sink);

        sink.Texts["labels/train/a.txt"].Trim().Split('\n').Should().HaveCount(1);
    }

    [Fact]
    public void ImageFolder_puts_images_under_split_and_class()
    {
        var sink = new MemorySink();
        DatasetExportWriter.Write(new ExportManifest("c", TaskType.Classification, ["ok", "ng"], [
            Img("a.jpg", DatasetSplit.Train, Cls("ok")),
            Img("b.jpg", DatasetSplit.Val, Cls("ng")),
            Img("nolabel.jpg", DatasetSplit.Train),
        ]), "imagefolder", sink);

        sink.Images.Select(i => i.Path).Should().BeEquivalentTo(["train/ok/a.jpg", "val/ng/b.jpg"]);
    }

    [Fact]
    public void Mvtec_separates_normal_train_from_defect_test()
    {
        var sink = new MemorySink();
        DatasetExportWriter.Write(new ExportManifest("a", TaskType.Anomaly, ["good", "scratch"], [
            Img("n1.jpg", DatasetSplit.Train, Cls("good")),
            Img("n2.jpg", DatasetSplit.Val, Cls("good")),
            Img("d1.jpg", DatasetSplit.Val, Cls("scratch")),
        ]), "mvtec", sink);

        // 학습은 train/good 만 본다. 이상 샘플은 평가용으로 test 아래에 간다.
        sink.Images.Select(i => i.Path).Should().BeEquivalentTo(["train/good/n1.jpg", "test/good/n2.jpg", "test/scratch/d1.jpg"]);
    }

    [Fact]
    public void PpOcr_writes_rec_lists_and_dictionary()
    {
        var text = new LabelAnnotation
        {
            Shape = AnnotationShape.Text, ClassName = "text",
            Box = new NormBox(0.1, 0.1, 0.5, 0.2), Text = "AB12",
        };
        var sink = new MemorySink();
        DatasetExportWriter.Write(new ExportManifest("o", TaskType.Ocr, ["text"],
            [Img("crop.jpg", DatasetSplit.Train, text)]), "ppocr", sink);

        sink.Images.Single().Path.Should().Be("train_crops/crop.jpg");
        sink.Texts["train_rec.txt"].Trim().Should().Be("train_crops/crop.jpg\tAB12");
        sink.Texts["val_rec.txt"].Should().BeEmpty();
        sink.Texts["dict.txt"].Split('\n').Should().BeEquivalentTo(["1", "2", "A", "B"]);
    }

    [Fact]
    public void Coco_writes_pixel_coordinates_and_categories()
    {
        var polygon = new LabelAnnotation
        {
            Shape = AnnotationShape.Polygon, ClassName = "defect",
            Polygon = [new NormPoint(0.25, 0.5), new NormPoint(0.5, 0.5), new NormPoint(0.5, 1.0)],
        };
        var sink = new MemorySink();
        DatasetExportWriter.Write(new ExportManifest("s", TaskType.Segmentation, ["good", "defect"],
            [Img("s.jpg", DatasetSplit.Train, polygon)]), "coco", sink);

        var json = sink.Texts["annotations/instances_train.json"];
        json.Should().Contain("\"category_id\":2");   // defect 는 인덱스 1 → COCO 는 1부터라 2
        json.Should().Contain("200");                  // 0.25 × 800 = 200 픽셀
        sink.Images.Single().Path.Should().Be("images/train/s.jpg");
    }

    [Fact]
    public void Class_names_with_path_characters_cannot_escape_the_folder()
    {
        DatasetExportWriter.SafeSegment("../../etc").Should().NotContain("/").And.NotContain("\\");
        DatasetExportWriter.SafeSegment("..").Should().Be("_");
        DatasetExportWriter.SafeSegment("  ").Should().Be("_");
        DatasetExportWriter.SafeSegment("정상 A").Should().Be("정상 A");
    }

    [Theory]
    [InlineData(TaskType.Detection, "yolo")]
    [InlineData(TaskType.Classification, "imagefolder")]
    [InlineData(TaskType.Anomaly, "mvtec")]
    [InlineData(TaskType.Ocr, "ppocr")]
    [InlineData(TaskType.Segmentation, "coco")]
    public void Default_format_matches_the_worker_expectation(TaskType taskType, string format)
    {
        DatasetExportWriter.DefaultFormat(taskType).Should().Be(format);
        // 워커가 스크립트별로 요구하는 형식과 같아야 배정이 통과한다
        var script = taskType switch
        {
            TaskType.Detection => TrainingScript.TrainDfine,
            TaskType.Classification => TrainingScript.TrainClassifier,
            TaskType.Anomaly => TrainingScript.TrainAnomaly,
            TaskType.Ocr => TrainingScript.TrainPpocr,
            _ => TrainingScript.TrainRfdetrSeg,
        };
        script.DatasetExportFormat().Should().Be(format);
    }
}

public class PerceptualHashTests
{
    private static byte[] Gradient(int shift = 0)
    {
        var gray = new byte[PerceptualHash.SampleWidth * PerceptualHash.SampleHeight];
        for (int i = 0; i < gray.Length; i++) gray[i] = (byte)((i * 7 + shift) % 256);
        return gray;
    }

    [Fact]
    public void Same_pixels_give_the_same_hash()
    {
        PerceptualHash.FromGrayscale9x8(Gradient()).Should().Be(PerceptualHash.FromGrayscale9x8(Gradient()));
    }

    [Fact]
    public void Small_brightness_shift_keeps_the_hash_near()
    {
        var a = PerceptualHash.FromGrayscale9x8(Gradient());
        var b = PerceptualHash.FromGrayscale9x8(Gradient(shift: 3));
        // dHash 는 이웃 픽셀의 대소만 보므로 밝기를 조금 올려도 거의 같다
        PerceptualHash.Distance(a, b).Should().BeLessThan(PerceptualHash.NearDuplicateThreshold);
    }

    [Fact]
    public void Different_pictures_are_far_apart()
    {
        var flat = new byte[72];
        var noisy = new byte[72];
        for (int i = 0; i < 72; i++) noisy[i] = (byte)(i % 2 == 0 ? 255 : 0);

        PerceptualHash.IsNearDuplicate(
            PerceptualHash.FromGrayscale9x8(flat),
            PerceptualHash.FromGrayscale9x8(noisy)).Should().BeFalse();
    }

    [Fact]
    public void Hex_round_trips()
    {
        var hash = PerceptualHash.FromGrayscale9x8(Gradient());
        PerceptualHash.FromHex(PerceptualHash.ToHex(hash)).Should().Be(hash);
        PerceptualHash.ToHex(hash).Should().HaveLength(16);
        PerceptualHash.FromHex(null).Should().Be(0UL);
    }

    [Fact]
    public void Wrong_sample_size_is_rejected()
    {
        var act = () => PerceptualHash.FromGrayscale9x8(new byte[10]);
        act.Should().Throw<ArgumentException>();
    }
}

public class LabelAnnotationTests
{
    [Fact]
    public void Box_requires_a_rectangle_inside_the_image()
    {
        new LabelAnnotation { Shape = AnnotationShape.Box, ClassName = "a", Box = new NormBox(0.1, 0.1, 0.2, 0.2) }
            .IsValid(out _).Should().BeTrue();

        new LabelAnnotation { Shape = AnnotationShape.Box, ClassName = "a" }.IsValid(out var e1).Should().BeFalse();
        e1.Should().Contain("사각형");

        new LabelAnnotation { Shape = AnnotationShape.Box, ClassName = "a", Box = new NormBox(0.9, 0.1, 0.5, 0.2) }
            .IsValid(out var e2).Should().BeFalse();
        e2.Should().Contain("밖으로");

        new LabelAnnotation { Shape = AnnotationShape.Box, ClassName = "a", Box = new NormBox(0.1, 0.1, 0, 0.2) }
            .IsValid(out var e3).Should().BeFalse();
        e3.Should().Contain("0보다");
    }

    [Fact]
    public void Text_requires_the_recognised_string()
    {
        var withoutText = new LabelAnnotation { Shape = AnnotationShape.Text, ClassName = "t", Box = new NormBox(0, 0, 0.5, 0.5) };
        withoutText.IsValid(out var e).Should().BeFalse();
        e.Should().Contain("텍스트");

        (withoutText with { Text = "AB" }).IsValid(out _).Should().BeTrue();
    }

    [Fact]
    public void Polygon_needs_three_points()
    {
        new LabelAnnotation { Shape = AnnotationShape.Polygon, ClassName = "p", Polygon = [new(0, 0), new(1, 0)] }
            .IsValid(out var e).Should().BeFalse();
        e.Should().Contain("3개");
    }

    [Fact]
    public void Classification_needs_only_a_class()
    {
        new LabelAnnotation { Shape = AnnotationShape.Classification, ClassName = "ok" }.IsValid(out _).Should().BeTrue();
        new LabelAnnotation { Shape = AnnotationShape.Classification, ClassName = " " }.IsValid(out _).Should().BeFalse();
    }

    [Fact]
    public void Payload_round_trips_through_json()
    {
        var original = new LabelAnnotation
        {
            Shape = AnnotationShape.Text, ClassName = "text",
            Box = new NormBox(0.1, 0.2, 0.3, 0.4), Text = "가나다 123",
        };
        var back = AnnotationPayload.FromJson(original.Shape, original.ClassName, AnnotationPayload.ToJson(original));
        back.Should().BeEquivalentTo(original);

        var polygon = new LabelAnnotation
        {
            Shape = AnnotationShape.Polygon, ClassName = "p",
            Polygon = [new NormPoint(0.1, 0.1), new NormPoint(0.9, 0.1), new NormPoint(0.5, 0.9)],
        };
        AnnotationPayload.FromJson(polygon.Shape, polygon.ClassName, AnnotationPayload.ToJson(polygon))
            .Should().BeEquivalentTo(polygon);
    }

    [Fact]
    public void Broken_payload_is_dropped_rather_than_throwing()
    {
        AnnotationPayload.FromJson(AnnotationShape.Box, "a", "{ not json").Should().BeNull();
        AnnotationPayload.FromJson(AnnotationShape.Classification, "a", null)!.ClassName.Should().Be("a");
    }

    [Theory]
    [InlineData(TaskType.Detection, AnnotationShape.Box, true)]
    [InlineData(TaskType.Detection, AnnotationShape.Classification, false)]
    [InlineData(TaskType.Classification, AnnotationShape.Classification, true)]
    [InlineData(TaskType.Classification, AnnotationShape.Box, false)]
    [InlineData(TaskType.Ocr, AnnotationShape.Text, true)]
    [InlineData(TaskType.Segmentation, AnnotationShape.Polygon, true)]
    public void Task_type_limits_which_shapes_are_allowed(TaskType taskType, AnnotationShape shape, bool allowed) =>
        taskType.AllowedFor().Contains(shape).Should().Be(allowed);
}
