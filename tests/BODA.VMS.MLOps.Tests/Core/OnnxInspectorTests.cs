using BODA.VMS.MLOps.Core.Domain;
using BODA.VMS.MLOps.Core.Onnx;
using BODA.VMS.MLOps.Tests.TestAssets;
using FluentAssertions;

namespace BODA.VMS.MLOps.Tests.Core;

/// <summary>규약 계약 테스트 (개발 문서 §11) — 스텁 ONNX 로 판별·메타 파싱 회귀</summary>
public class OnnxInspectorTests
{
    [Fact]
    public void ShapeReader_reads_names_and_dims()
    {
        using var m = new OnnxStubs.TempOnnx(OnnxStubs.YoloStub, "yolo");
        var (inputs, outputs) = OnnxGraphShapeReader.Read(m.Path);
        inputs.Should().ContainSingle(i => i.Name == "images");
        inputs[0].Dims.Should().Equal(1, 3, 640, 640);
        outputs.Should().ContainSingle(o => o.Name == "output0");
        outputs[0].Dims.Should().Equal(1, 6, 8);
    }

    [Fact]
    public void Inspect_dfine_with_metadata()
    {
        using var m = new OnnxStubs.TempOnnx(OnnxStubs.DeployWithMeta, "meta");
        var r = OnnxModelInspector.Inspect(m.Path);
        r.IsValidProtobuf.Should().BeTrue();
        r.Format.Should().Be(ModelFormat.DFine);
        r.Classes.Should().Equal("good", "defect");
        r.InputSize.Should().Be(640);
        r.Metadata["model_format"].Should().Be("dfine");
        r.Warnings.Should().BeEmpty();
    }

    [Fact]
    public void Inspect_dfine_without_metadata_uses_graph_layout_and_input_dims()
    {
        using var m = new OnnxStubs.TempOnnx(OnnxStubs.DeployNoMeta, "nometa");
        var r = OnnxModelInspector.Inspect(m.Path);
        r.Format.Should().Be(ModelFormat.DFine);
        r.Classes.Should().BeNull();
        r.InputSize.Should().Be(640, "images[1,3,640,640] 에서 유추");
        r.Warnings.Should().Contain(w => w.Contains("names"));
    }

    [Fact]
    public void Inspect_hf_raw_layout_is_dfine()
    {
        using var m = new OnnxStubs.TempOnnx(OnnxStubs.RawHf, "raw");
        OnnxModelInspector.Inspect(m.Path).Format.Should().Be(ModelFormat.DFine);
    }

    [Fact]
    public void Inspect_yolo_warns_license()
    {
        using var m = new OnnxStubs.TempOnnx(OnnxStubs.YoloStub, "yolo");
        var r = OnnxModelInspector.Inspect(m.Path);
        r.Format.Should().Be(ModelFormat.Yolo);
        r.Classes.Should().Equal("good", "defect");
        r.Warnings.Should().Contain(w => w.Contains("YOLO"));
    }

    [Fact]
    public void Inspect_garbage_is_invalid()
    {
        var path = Path.Combine(Path.GetTempPath(), $"mlops-garbage-{Guid.NewGuid():N}.onnx");
        File.WriteAllText(path, "this is not protobuf at all, just text that is long enough to be read as bytes");
        try
        {
            var r = OnnxModelInspector.Inspect(path);
            r.IsValidProtobuf.Should().BeFalse();
            r.Format.Should().Be(ModelFormat.Unknown);
        }
        finally { File.Delete(path); }
    }

    /// <summary>
    /// 조작된 varint 길이(2^63 이상)는 (long) 캐스팅 시 음수가 되어 끝 오프셋이 현재 위치보다 앞에 놓인다.
    /// 그 값으로 Stream.Position 을 되감으면 파싱이 영원히 반복된다 — 업로드 한 번으로 스레드를 점유하는 DoS.
    /// 파일은 15바이트지만 고치기 전에는 이 테스트가 시간 초과로 실패한다.
    /// </summary>
    [Fact]
    public async Task Oversized_varint_length_does_not_hang()
    {
        // graph(7,len=11) → input(11,len=거대) : 길이가 남은 바이트를 넘고 long 으로는 음수가 된다
        byte[] evil = [0x3A, 0x0B, 0x5A, 0x0B, 0x12, 0xF5, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0x01];
        var result = await WithinTimeoutAsync(evil, p => (object)OnnxModelInspector.Inspect(p));
        ((OnnxInspection)result).Format.Should().Be(ModelFormat.Unknown);
    }

    /// <summary>잘린 파일·쓰레기 바이트로도 멈추거나 예외가 새지 않아야 한다</summary>
    [Theory]
    [InlineData(new byte[] { 0x3A })]
    [InlineData(new byte[] { 0x3A, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0x7F })]
    [InlineData(new byte[] { 0x0A, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x01 })]
    [InlineData(new byte[] { 0x3A, 0x04, 0x5A, 0x02, 0x0A, 0xFF })]
    [InlineData(new byte[] { 0x72, 0x08, 0x0A, 0xFF, 0xFF, 0xFF, 0x0F, 0x12, 0x01, 0x61 })]
    public async Task Malformed_files_return_promptly(byte[] bytes) =>
        (await WithinTimeoutAsync(bytes, p => (object)OnnxSafeReader.Read(p))).Should().NotBeNull();

    /// <summary>파서를 별도 스레드에서 돌려, 멈추면 테스트가 매달리지 않고 실패하게 한다</summary>
    private static async Task<object> WithinTimeoutAsync(byte[] bytes, Func<string, object> parse)
    {
        var path = Path.Combine(Path.GetTempPath(), $"mlops-malformed-{Guid.NewGuid():N}.onnx");
        await File.WriteAllBytesAsync(path, bytes);
        try
        {
            var work = Task.Run(() => parse(path));
            var winner = await Task.WhenAny(work, Task.Delay(TimeSpan.FromSeconds(10)));
            winner.Should().BeSameAs(work, "조작된 길이 필드에도 파싱이 즉시 끝나야 한다");
            return await work;
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Reader_still_parses_valid_metadata_and_shapes_in_one_pass()
    {
        using var m = new OnnxStubs.TempOnnx(OnnxStubs.DeployWithMeta, "onepass");
        var info = OnnxSafeReader.Read(m.Path);
        info.Metadata["model_format"].Should().Be("dfine");
        info.Metadata["imgsz"].Should().Be("[640, 640]");
        info.Inputs.Select(i => i.Name).Should().Equal("images", "orig_target_sizes");
        info.Outputs.Select(o => o.Name).Should().Equal("labels", "boxes", "scores");
        info.Inputs[0].Dims.Should().Equal(1, 3, 640, 640);
    }

    [Theory]
    [InlineData("{0: 'good', 1: 'defect'}", new[] { "good", "defect" })]
    [InlineData("{1: 'b', 0: 'a'}", new[] { "a", "b" })]
    [InlineData("[\"x\", \"y\"]", new[] { "x", "y" })]
    [InlineData("{\"0\": \"p\", \"1\": \"q\"}", new[] { "p", "q" })]
    [InlineData("scratch, dent", new[] { "scratch", "dent" })]
    [InlineData("{0: \"dq\", 1: 'it\\'s'}", new[] { "dq", "it\\'s" })]
    public void ParseNames_accepts_python_repr_json_and_csv(string raw, string[] expected) =>
        OnnxModelInspector.ParseNames(raw).Should().Equal(expected);

    [Theory]
    [InlineData("[640, 640]", 640)]
    [InlineData("640", 640)]
    [InlineData("(224, 224)", 224)]
    [InlineData("", null)]
    public void ParseImgsz(string raw, int? expected) => OnnxModelInspector.ParseImgsz(raw).Should().Be(expected);

    [Fact]
    public void ProbeFormat_classifies_by_output_rank()
    {
        var meta = new Dictionary<string, string>();
        OnnxModelInspector.ProbeFormat(meta, [new("input", [1, 3, 224, 224])], [new("output", [1, 5])]).Should().Be(ModelFormat.Classifier);
        OnnxModelInspector.ProbeFormat(meta, [new("images", [1, 3, 640, 640])], [new("output0", [1, 38, 8400]), new("output1", [1, 32, 160, 160])])
            .Should().Be(ModelFormat.YoloSeg);
        OnnxModelInspector.ProbeFormat(meta, [new("input", [1, 3, 256, 256])], [new("output", [1, 1, 256, 256])]).Should().Be(ModelFormat.Anomaly);
        OnnxModelInspector.ProbeFormat(new Dictionary<string, string> { ["model_format"] = "ppocr" }, [], []).Should().Be(ModelFormat.PpOcr);
        OnnxModelInspector.ProbeFormat(meta, [new("a", [])], [new("b", []), new("c", []), new("d", [])]).Should().Be(ModelFormat.Unknown);
    }

    /// <summary>
    /// RF-DETR 세그멘테이션 내보내기 (roboflow/rf-detr 의 export 가 정하는 이름 그대로).
    /// dets·labels 만 있으면 검출 전용이라 세그가 아니다 — masks 가 4차원으로 함께 있어야 한다.
    /// </summary>
    [Fact]
    public void ProbeFormat_reads_rfdetr_segmentation()
    {
        var meta = new Dictionary<string, string>();
        OnnxTensorInfo[] segOutputs =
        [
            new("dets", [1, 300, 4]), new("labels", [1, 300, 3]), new("masks", [1, 300, 150, 150]),
        ];

        OnnxModelInspector.ProbeFormat(meta, [new("input", [1, 3, 560, 560])], segOutputs)
            .Should().Be(ModelFormat.RfdetrSeg);

        // 검출 전용 RF-DETR 은 세그가 아니다
        OnnxModelInspector.ProbeFormat(meta, [new("input", [1, 3, 560, 560])],
            [new("dets", [1, 300, 4]), new("labels", [1, 300, 3])])
            .Should().NotBe(ModelFormat.RfdetrSeg);

        // 이름만 맞고 모양이 다른 파일을 세그로 등록하면 라인에서 마스크를 읽다 터진다
        OnnxModelInspector.ProbeFormat(meta, [new("input", [1, 3, 560, 560])],
            [new("dets", [1, 300, 4]), new("labels", [1, 300, 3]), new("masks", [1, 300, 150])])
            .Should().NotBe(ModelFormat.RfdetrSeg);

        // metadata 로도 읽는다 (스크립트가 새겨 넣는 값)
        OnnxModelInspector.ProbeFormat(new Dictionary<string, string> { ["model_format"] = "rfdetrseg" }, [], [])
            .Should().Be(ModelFormat.RfdetrSeg);
    }

    /// <summary>
    /// 스크립트가 낸 규약과 레지스트리가 판별한 규약이 어긋나면 아티팩트 등록에서 걸러야 한다.
    /// 전에는 세그 스크립트만 Unknown 이라 그 대조가 통째로 비어 있었다.
    /// </summary>
    [Fact]
    public void Every_training_script_declares_the_format_it_produces()
    {
        foreach (var script in Enum.GetValues<TrainingScript>())
            script.ExpectedFormat().Should().NotBe(ModelFormat.Unknown, "{0} 가 내는 규약을 대조할 수 없다", script);
    }
}
