using BODA.VMS.MLOps.Core.Domain;
using BODA.VMS.MLOps.Core.Training;
using FluentAssertions;

namespace BODA.VMS.MLOps.Tests.Core;

/// <summary>
/// 증강 프리셋 — 누르면 그 스크립트가 실제로 쓰는 값만 바뀌고, 그 값은 화이트리스트를 통과해야 한다.
/// 쓰지 않는 값을 바꾸는 버튼은 "조명에 강하게 했다" 고 사람을 속인다.
/// </summary>
public class AugmentationPresetTests
{
    public static TheoryData<TrainingScript> Scripts => new(Enum.GetValues<TrainingScript>());

    [Theory]
    [MemberData(nameof(Scripts))]
    public void Every_preset_passes_the_whitelist(TrainingScript script)
    {
        foreach (var preset in AugmentationPresets.For(script))
        {
            HyperparamWhitelist.ValidateNormalized(script, preset.Values, out var errors)
                .Should().BeTrue($"{script} · {preset.Name}: {string.Join("; ", errors)}");
        }
    }

    [Theory]
    [MemberData(nameof(Scripts))]
    public void Presets_only_touch_keys_the_script_actually_uses(TrainingScript script)
    {
        var keys = AugmentationPresets.EffectiveKeys(script);
        foreach (var preset in AugmentationPresets.For(script))
            preset.Values.Keys.Should().BeSubsetOf(keys, $"{script} · {preset.Name}");
    }

    [Fact]
    public void Dfine_gets_colour_only_because_it_ignores_mosaic_and_mixup()
    {
        // train_dfine.py 는 mosaic·mixup 을 받고 버린다 ("D-FINE 학습은 mosaic/mixup 을 사용하지 않습니다 (무시)")
        var lighting = AugmentationPresets.For(TrainingScript.TrainDfine).Single(p => p.Name == "공장 조명");
        lighting.Values.Should().BeEquivalentTo(new Dictionary<string, string> { ["hsv_h"] = "0.02", ["hsv_s"] = "0.8", ["hsv_v"] = "0.7" });
    }

    [Fact]
    public void Yolo_gets_the_wpf_values()
    {
        var lighting = AugmentationPresets.For(TrainingScript.TrainYolo).Single(p => p.Name == "공장 조명");
        lighting.Values.Should().BeEquivalentTo(new Dictionary<string, string>
            { ["mosaic"] = "1", ["mixup"] = "0.1", ["hsv_h"] = "0.02", ["hsv_s"] = "0.8", ["hsv_v"] = "0.7" });
        AugmentationPresets.For(TrainingScript.TrainYolo).Select(p => p.Name).Should().Equal("기본", "공장 조명", "작은 결함", "최소");
    }

    [Theory]
    [InlineData(TrainingScript.TrainRfdetrSeg)]   // 증강 인자를 받기만 하고 쓰지 않는다
    [InlineData(TrainingScript.TrainClassifier)]
    [InlineData(TrainingScript.TrainAnomaly)]
    [InlineData(TrainingScript.TrainPpocr)]
    public void Scripts_without_effective_augmentation_show_no_presets(TrainingScript script)
        => AugmentationPresets.For(script).Should().BeEmpty();
}
