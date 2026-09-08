using System.Text.Json;
using BODA.VMS.MLOps.Core.Domain;
using BODA.VMS.MLOps.Core.Training;
using FluentAssertions;
using VMS.Core.Services;

namespace BODA.VMS.MLOps.Tests.Core;

public class ModelReferenceTests
{
    [Fact]
    public void Parses_pinned_and_production()
    {
        var id = Guid.NewGuid();
        ModelReference.TryParse($"model://{id}@3", out var pinned).Should().BeTrue();
        pinned!.Version.Should().Be(3);
        pinned.FollowsProduction.Should().BeFalse();

        ModelReference.TryParse($"MODEL://{id}@production", out var prod).Should().BeTrue();
        prod!.FollowsProduction.Should().BeTrue();

        ModelReference.TryParse($"model://{id}@v7", out var v).Should().BeTrue();
        v!.Version.Should().Be(7);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("C:\\models\\best.onnx")]
    [InlineData("model://not-a-guid@1")]
    [InlineData("model://00000000-0000-0000-0000-000000000000@0")]
    [InlineData("model://00000000-0000-0000-0000-000000000000@")]
    [InlineData("model://00000000-0000-0000-0000-000000000000")]
    public void Rejects_invalid(string? value) => ModelReference.TryParse(value, out _).Should().BeFalse();

    [Fact]
    public void RoundTrips_ToString()
    {
        var r = new ModelReference(Guid.NewGuid(), null);
        ModelReference.Parse(r.ToString()).Should().Be(r);
        var p = new ModelReference(Guid.NewGuid(), 12);
        ModelReference.Parse(p.ToString()).Should().Be(p);
    }
}

public class TrainingJobStateMachineTests
{
    [Theory]
    [InlineData(TrainingJobState.Queued, TrainingJobState.Assigned, true)]
    [InlineData(TrainingJobState.Assigned, TrainingJobState.Preparing, true)]
    [InlineData(TrainingJobState.Preparing, TrainingJobState.Running, true)]
    [InlineData(TrainingJobState.Running, TrainingJobState.Exporting, true)]
    [InlineData(TrainingJobState.Exporting, TrainingJobState.Uploading, true)]
    [InlineData(TrainingJobState.Uploading, TrainingJobState.Succeeded, true)]
    [InlineData(TrainingJobState.Running, TrainingJobState.Cancelled, true)]
    [InlineData(TrainingJobState.Failed, TrainingJobState.Queued, true)]
    [InlineData(TrainingJobState.Queued, TrainingJobState.Running, false)]
    [InlineData(TrainingJobState.Succeeded, TrainingJobState.Queued, false)]
    [InlineData(TrainingJobState.Cancelled, TrainingJobState.Queued, false)]
    [InlineData(TrainingJobState.Running, TrainingJobState.Preparing, false)]
    public void Transitions(TrainingJobState from, TrainingJobState to, bool ok) =>
        TrainingJobStateMachine.CanTransition(from, to).Should().Be(ok);

    [Fact]
    public void Terminal_and_active()
    {
        TrainingJobStateMachine.IsTerminal(TrainingJobState.Succeeded).Should().BeTrue();
        TrainingJobStateMachine.IsTerminal(TrainingJobState.Running).Should().BeFalse();
        TrainingJobStateMachine.IsActiveOnWorker(TrainingJobState.Uploading).Should().BeTrue();
        TrainingJobStateMachine.IsActiveOnWorker(TrainingJobState.Queued).Should().BeFalse();
        TrainingJobStateMachine.IsWorkerReportable(TrainingJobState.Succeeded).Should().BeFalse();
    }
}

public class HyperparamWhitelistTests
{
    private static JsonElement J(string json) => JsonDocument.Parse(json).RootElement.Clone();

    [Fact]
    public void Accepts_known_keys_and_normalizes()
    {
        var r = HyperparamWhitelist.Validate(TrainingScript.TrainDfine, J("""{"epochs": 10, "lr": "0.00025", "hsv_v": 0.4, "batch_size": 8}"""), out var errors);
        errors.Should().BeEmpty();
        r.Should().NotBeNull();
        r!["epochs"].Should().Be("10");
        r["lr"].Should().Be("0.00025");
        r["hsv_v"].Should().Be("0.4");
    }

    [Fact]
    public void Rejects_unknown_key_type_and_range()
    {
        HyperparamWhitelist.Validate(TrainingScript.TrainDfine, J("""{"epochs": 10, "__import__": "os"}"""), out var e1).Should().BeNull();
        e1.Should().ContainSingle(x => x.Contains("__import__"));

        HyperparamWhitelist.Validate(TrainingScript.TrainDfine, J("""{"epochs": "ten"}"""), out var e2).Should().BeNull();
        e2.Should().ContainSingle(x => x.Contains("epochs"));

        HyperparamWhitelist.Validate(TrainingScript.TrainDfine, J("""{"batch_size": 0}"""), out var e3).Should().BeNull();
        e3.Should().ContainSingle();

        HyperparamWhitelist.Validate(TrainingScript.TrainAnomaly, J("""{"method": "rm -rf"}"""), out var e4).Should().BeNull();
        e4.Should().ContainSingle(x => x.Contains("허용값"));

        // 스크립트별 화이트리스트: classifier 에는 mosaic 없음
        HyperparamWhitelist.Validate(TrainingScript.TrainClassifier, J("""{"mosaic": 1.0}"""), out var e5).Should().BeNull();
        e5.Should().ContainSingle();
    }

    [Fact]
    public void Null_or_empty_is_ok()
    {
        HyperparamWhitelist.Validate(TrainingScript.TrainYolo, null, out var e).Should().BeEmpty();
        e.Should().BeEmpty();
        HyperparamWhitelist.Validate(TrainingScript.TrainYolo, J("{}"), out _).Should().BeEmpty();
    }

    [Fact]
    public void ValidateNormalized_round_trip()
    {
        var hp = new Dictionary<string, string> { ["epochs"] = "3", ["method"] = "patchcore", ["coreset_ratio"] = "0.1" };
        HyperparamWhitelist.ValidateNormalized(TrainingScript.TrainAnomaly, hp, out var errors).Should().BeTrue();
        errors.Should().BeEmpty();
        hp["method"] = "evil";
        HyperparamWhitelist.ValidateNormalized(TrainingScript.TrainAnomaly, hp, out errors).Should().BeFalse();
    }
}

public class TrainingArgumentListTests
{
    [Fact]
    public void Builds_argument_list_without_string_concatenation()
    {
        var args = TrainingArgumentList.Build(new TrainingArgumentList.Request(
            TrainingScript.TrainDfine, @"C:\w\scripts\train_dfine.py", @"D:\", @"C:\w\jobs\1\output\", @"C:\w\pretrained\dfine-s",
            Seed: 42, Hyperparams: new Dictionary<string, string> { ["epochs"] = "5", ["lr"] = "0.00025", ["hsv_v"] = "0.4" }));

        args[0].Should().Be(@"C:\w\scripts\train_dfine.py");
        args.Should().ContainInOrder("--dataset", @"D:\.");           // 드라이브 루트 규칙
        args.Should().ContainInOrder("--output", @"C:\w\jobs\1\output"); // 후행 구분자 제거
        args.Should().ContainInOrder("--epochs", "5");
        args.Should().ContainInOrder("--lr", "0.00025");
        args.Should().ContainInOrder("--hsv_v", "0.4");
        args.Should().ContainInOrder("--pretrained", @"C:\w\pretrained\dfine-s");
        args.Should().ContainInOrder("--seed", "42");
        args.Should().Contain("--export_onnx");
        args.Should().NotContain(a => a.Contains(' ') && a.StartsWith("--"), "인자는 항상 개별 토큰");
    }

    [Fact]
    public void Ppocr_target_goes_first_and_no_seed()
    {
        var args = TrainingArgumentList.Build(new TrainingArgumentList.Request(
            TrainingScript.TrainPpocr, "train_ppocr.py", "ds", "out", null, 0,
            new Dictionary<string, string> { ["target"] = "detection", ["epochs"] = "20" }));
        args[1].Should().Be("--target");
        args[2].Should().Be("detection");
        args.Should().NotContain("--seed");
    }

    [Fact]
    public void NormalizeDir_matches_contracts_rule()
    {
        TrainingArgumentList.NormalizeDir(@"D:\").Should().Be(TrainingArgumentBuilder.NormalizeDir(@"D:\"));
        TrainingArgumentList.NormalizeDir(@"C:\a\b\").Should().Be(TrainingArgumentBuilder.NormalizeDir(@"C:\a\b\"));
    }
}

public class TrainingProtocolTrackerTests
{
    [Fact]
    public void Tracks_protocol_lines()
    {
        var t = new TrainingProtocolTracker();
        t.Apply("[INFO] hello").Kind.Should().Be(TrainingOutputKind.None);
        t.Apply("[EPOCH] 2/10").Kind.Should().Be(TrainingOutputKind.Epoch);
        t.CurrentEpoch.Should().Be(2);
        t.TotalEpochs.Should().Be(10);
        t.Apply("[LOSS] 0.1234").Loss.Should().Be(0.1234);
        t.Apply("[ACC] 0.87").Metric.Should().Be(0.87);
        t.Apply("[PROGRESS] 45.5").Progress.Should().Be(45.5);
        t.Apply(@"[ONNX] C:\out\best.onnx").OnnxPath.Should().Be(@"C:\out\best.onnx");
        t.Apply("[ERROR] boom").Error.Should().Be("boom");
        t.Apply("[DONE]").Kind.Should().Be(TrainingOutputKind.Done);
        t.SawDone.Should().BeTrue();
        TrainingProtocolTracker.IsCudaOom("RuntimeError: CUDA out of memory. Tried to allocate").Should().BeTrue();
        TrainingProtocolTracker.IsCudaOom("[EPOCH] 1/2").Should().BeFalse();
    }
}
