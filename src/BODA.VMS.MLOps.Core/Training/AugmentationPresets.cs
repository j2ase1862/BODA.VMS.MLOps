using System.Globalization;
using BODA.VMS.MLOps.Core.Domain;

namespace BODA.VMS.MLOps.Core.Training;

/// <summary>증강 값 묶음 하나 — 학습 제출 화면의 버튼 하나가 된다.</summary>
public sealed record AugmentationPreset(string Name, string Purpose, IReadOnlyDictionary<string, string> Values);

/// <summary>
/// 증강 프리셋 (개발 문서 §5.5 5번) — WPF 도구의 기본 · 공장 조명 · 작은 결함 · 최소 를 옮긴 것.
///
/// <para><b>스크립트가 실제로 쓰는 값만 바꾼다.</b> WPF 는 스크립트를 가리지 않고 다섯 값을 모두 채웠지만,
/// <c>train_dfine.py</c> 는 mosaic·mixup 을 받고 버리며(경고만 낸다), <c>train_rfdetr_seg.py</c> 는 증강 인자를 받기만 하고
/// 하나도 쓰지 않는다. 쓰지 않는 값을 바꾸는 버튼은 "조명 변화에 강하게 했다" 고 사람을 속인다.
/// 스크립트가 증강을 바꾸면 <see cref="EffectiveKeys"/> 를 함께 고친다.</para>
/// <para>값은 화이트리스트 범위 안이어야 한다 — 시험이 프리셋마다 <see cref="HyperparamWhitelist"/> 로 검사한다.</para>
/// </summary>
public static class AugmentationPresets
{
    /// <summary>그 스크립트가 학습에 실제로 반영하는 증강 키. 비면 프리셋을 보이지 않는다.</summary>
    public static IReadOnlyList<string> EffectiveKeys(TrainingScript script) => script switch
    {
        TrainingScript.TrainYolo => ["mosaic", "mixup", "hsv_h", "hsv_s", "hsv_v"],
        TrainingScript.TrainDfine => ["hsv_h", "hsv_s", "hsv_v"],
        _ => [],
    };

    // WPF 도구(VMS.DeepLearning, LabelingMainViewModel 의 PresetAug*)와 같은 값
    private static readonly (string Name, string Purpose, (string Key, double Value)[] Values)[] All =
    [
        ("기본", "균형 잡힌 일반 학습", [("mosaic", 1.0), ("mixup", 0.0), ("hsv_h", 0.015), ("hsv_s", 0.7), ("hsv_v", 0.4)]),
        ("공장 조명", "조명이 자주 바뀌는 라인 — 밝기(hsv_v)를 크게 흔든다", [("mosaic", 1.0), ("mixup", 0.1), ("hsv_h", 0.02), ("hsv_s", 0.8), ("hsv_v", 0.7)]),
        ("작은 결함", "작은 결함 — 문맥을 다양하게 섞는다", [("mosaic", 1.0), ("mixup", 0.15), ("hsv_h", 0.015), ("hsv_s", 0.7), ("hsv_v", 0.5)]),
        ("최소", "데이터가 이미 많거나 증강이 오히려 해로울 때", [("mosaic", 0.5), ("mixup", 0.0), ("hsv_h", 0.005), ("hsv_s", 0.3), ("hsv_v", 0.2)]),
    ];

    /// <summary>그 스크립트에서 쓸 수 있는 프리셋. 값은 그 스크립트가 쓰는 키만 담는다.</summary>
    public static IReadOnlyList<AugmentationPreset> For(TrainingScript script)
    {
        var keys = EffectiveKeys(script);
        if (keys.Count == 0) return [];
        return All.Select(p => new AugmentationPreset(p.Name, p.Purpose,
                p.Values.Where(v => keys.Contains(v.Key))
                    .ToDictionary(v => v.Key, v => v.Value.ToString(CultureInfo.InvariantCulture), StringComparer.Ordinal)))
            .ToList();
    }
}
