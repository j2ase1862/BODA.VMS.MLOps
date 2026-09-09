using BODA.VMS.MLOps.Core.Monitoring;
using FluentAssertions;

namespace BODA.VMS.MLOps.Tests.Core;

/// <summary>
/// 라인에 나간 모델이 나빠졌는지 보는 판단.
///
/// <para>
/// 이 판단이 헐거우면 표본이 적을 때마다 경고가 울리고, 몇 번 헛경고가 나면 사람은 경고를
/// 통째로 무시한다 — 그때부터 진짜 문제도 지나친다. 반대로 너무 빡빡하면 나빠진 모델이
/// 계속 라인에 남는다. 그래서 <b>언제 울리고 언제 안 울리는지</b> 를 양쪽 다 못 박는다.
/// </para>
/// </summary>
public class ModelHealthTests
{
    private static OutcomeWindow Window(int total, int ng, double? confidence = null, int? confidenceSamples = null,
        double? brightness = null, double? focus = null)
        => new(total, ng, confidence, confidenceSamples ?? (confidence is null ? 0 : total), brightness, focus);

    // ───────────── 표본이 모자랄 때 ─────────────

    /// <summary>모르는 것을 "괜찮다" 로 말하면 안 된다.</summary>
    [Fact]
    public void Too_few_samples_is_not_the_same_as_healthy()
    {
        // 20건 중 1건 → 2건. 5%p 올랐지만 그냥 흔들림이다.
        var verdict = ModelHealth.Evaluate(Window(20, 1), Window(20, 2));

        verdict.Signal.Should().Be(ModelHealthSignal.NotEnoughData);
        verdict.Detail.Should().Contain("아직 판단할 수 없다");
        verdict.SuggestRetrain.Should().BeFalse();
    }

    [Fact]
    public void Both_windows_must_have_enough_samples()
    {
        ModelHealth.Evaluate(Window(1000, 10), Window(50, 20)).Signal.Should().Be(ModelHealthSignal.NotEnoughData);
        ModelHealth.Evaluate(Window(50, 1), Window(1000, 200)).Signal.Should().Be(ModelHealthSignal.NotEnoughData);
    }

    // ───────────── 불량률 ─────────────

    /// <summary>표본이 넉넉하고 차이가 뚜렷하면 울려야 한다.</summary>
    [Fact]
    public void Clear_rise_with_enough_samples_is_flagged()
    {
        var verdict = ModelHealth.Evaluate(Window(2000, 40), Window(2000, 200));   // 2% → 10%

        verdict.Signal.Should().Be(ModelHealthSignal.NgRateRose);
        verdict.BaselineNgRate.Should().BeApproximately(0.02, 1e-9);
        verdict.RecentNgRate.Should().BeApproximately(0.10, 1e-9);
        verdict.PValue.Should().NotBeNull();
        verdict.PValue!.Value.Should().BeLessThan(ModelHealth.SignificanceLevel);
        verdict.SuggestRetrain.Should().BeTrue();
    }

    /// <summary>
    /// 통계적으로는 유의하지만 실무에서 의미가 없는 상승은 울리지 않는다.
    /// 표본이 아주 크면 0.5%p 차이도 유의해진다 — 그것까지 울리면 경고가 소음이 된다.
    /// </summary>
    [Fact]
    public void Statistically_significant_but_tiny_rise_is_not_flagged()
    {
        // 5만 건씩 — 2.0% → 2.6%. p 는 작지만 상승폭이 문턱(2%p) 아래다.
        var verdict = ModelHealth.Evaluate(Window(50_000, 1000), Window(50_000, 1300));

        verdict.PValue!.Value.Should().BeLessThan(ModelHealth.SignificanceLevel, "표본이 크면 유의해진다");
        verdict.Signal.Should().Be(ModelHealthSignal.Steady, "그래도 0.6%p 상승으로 사람을 부르지 않는다");
    }

    /// <summary>상승폭은 크지만 우연으로 설명되는 경우 — 울리면 헛경고다.</summary>
    [Fact]
    public void Large_looking_rise_that_could_be_noise_is_not_flagged()
    {
        // 딱 문턱을 넘는 표본에서 5% → 8%. 흔들림으로 설명된다.
        var verdict = ModelHealth.Evaluate(Window(100, 5), Window(100, 8));

        verdict.PValue!.Value.Should().BeGreaterThan(ModelHealth.SignificanceLevel);
        verdict.Signal.Should().Be(ModelHealthSignal.Steady);
        verdict.SuggestRetrain.Should().BeFalse();
    }

    [Fact]
    public void Falling_ng_rate_is_not_an_alarm()
    {
        var verdict = ModelHealth.Evaluate(Window(2000, 200), Window(2000, 40));   // 10% → 2%

        verdict.Signal.Should().Be(ModelHealthSignal.Steady);
        verdict.SuggestRetrain.Should().BeFalse();
    }

    // ───────────── 입력이 달라진 경우 ─────────────

    /// <summary>
    /// 입력이 달라졌으면 그것부터 말한다. 모델을 다시 학습해도 낫지 않는 종류라,
    /// 재학습을 권하면 몇 시간을 버리고 같은 자리로 돌아온다.
    /// </summary>
    [Fact]
    public void Input_drift_is_reported_before_the_ng_rate()
    {
        var baseline = Window(2000, 40, brightness: 120);
        var recent = Window(2000, 200, brightness: 60);   // 불량률도 올랐지만 밝기가 반으로

        var verdict = ModelHealth.Evaluate(baseline, recent);

        verdict.Signal.Should().Be(ModelHealthSignal.InputDrifted);
        verdict.Detail.Should().Contain("조명");
        verdict.SuggestRetrain.Should().BeFalse("조명을 고칠 일이지 모델을 다시 학습할 일이 아니다");
    }

    [Fact]
    public void Focus_drift_points_at_the_lens()
    {
        var verdict = ModelHealth.Evaluate(
            Window(2000, 40, focus: 50), Window(2000, 200, focus: 20));

        verdict.Signal.Should().Be(ModelHealthSignal.InputDrifted);
        verdict.Detail.Should().Contain("초점");
    }

    /// <summary>조금 움직인 것으로 입력 탓을 하면 진짜 모델 문제를 가린다.</summary>
    [Fact]
    public void Small_input_movement_does_not_mask_a_model_problem()
    {
        var verdict = ModelHealth.Evaluate(
            Window(2000, 40, brightness: 120), Window(2000, 200, brightness: 126));   // 5% 움직임

        verdict.Signal.Should().Be(ModelHealthSignal.NgRateRose);
    }

    // ───────────── 신뢰도 ─────────────

    /// <summary>불량률이 아직 안 움직여도 신뢰도가 내려가면 앞선 신호다.</summary>
    [Fact]
    public void Confidence_drop_is_flagged_even_when_the_ng_rate_is_steady()
    {
        var verdict = ModelHealth.Evaluate(
            Window(2000, 40, confidence: 0.90), Window(2000, 44, confidence: 0.70));

        verdict.Signal.Should().Be(ModelHealthSignal.ConfidenceDropped);
        verdict.SuggestRetrain.Should().BeTrue();
    }

    /// <summary>
    /// 신뢰도를 보고한 건이 적으면 그 평균으로 판단하지 않는다.
    /// 검사는 2000건인데 신뢰도는 5건만 보고했다면 그 평균은 아무 말도 하지 않는다.
    /// </summary>
    [Fact]
    public void Confidence_from_too_few_reports_is_ignored()
    {
        var verdict = ModelHealth.Evaluate(
            Window(2000, 40, confidence: 0.90, confidenceSamples: 5),
            Window(2000, 44, confidence: 0.40, confidenceSamples: 5));

        verdict.Signal.Should().Be(ModelHealthSignal.Steady);
    }

    [Fact]
    public void Missing_confidence_is_simply_not_evaluated()
    {
        var verdict = ModelHealth.Evaluate(Window(2000, 40), Window(2000, 44));

        verdict.Signal.Should().Be(ModelHealthSignal.Steady);
    }

    // ───────────── p 값 자체 ─────────────

    /// <summary>같은 비율이면 우연일 확률이 높게 나와야 한다.</summary>
    [Fact]
    public void Identical_rates_give_a_high_p_value()
    {
        var p = ModelHealth.TwoProportionPValue(Window(1000, 50), Window(1000, 50));

        p.Should().NotBeNull();
        p!.Value.Should().BeApproximately(1.0, 1e-6);
    }

    /// <summary>알려진 값으로 계산이 맞는지 본다 — 손으로 계산한 z 와 맞아야 한다.</summary>
    [Fact]
    public void P_value_matches_a_hand_computed_case()
    {
        // 1000건 중 100건(10%) vs 1000건 중 150건(15%)
        // pooled = 250/2000 = 0.125, se = sqrt(0.125*0.875*(2/1000)) = 0.0147902
        // z = 0.05 / 0.0147902 = 3.3806 → 양측 p ≈ 0.000723
        var p = ModelHealth.TwoProportionPValue(Window(1000, 100), Window(1000, 150));

        p!.Value.Should().BeApproximately(0.000723, 5e-5);
    }

    /// <summary>
    /// 아주 작은 p 값이 "0.00%" 로 나가면 안 된다 — 계산이 안 된 것처럼 읽힌다.
    /// 정규 근사는 꼬리에서 정확히 0 으로 내려앉으므로 실제로 일어나는 일이다.
    /// </summary>
    [Fact]
    public void A_vanishing_p_value_is_worded_not_printed_as_zero()
    {
        // 2% → 20%, 5000건씩 — z 가 커서 근사가 0 을 낸다
        var verdict = ModelHealth.Evaluate(Window(5000, 100), Window(5000, 1000));

        verdict.Signal.Should().Be(ModelHealthSignal.NgRateRose);
        verdict.PValue!.Value.Should().Be(0);
        verdict.Detail.Should().Contain("0.01% 미만");
        verdict.Detail.Should().NotContain("0.00%");
    }

    /// <summary>양쪽 다 불량이 하나도 없으면 잴 수 없다 — 0 이 아니라 null 이다.</summary>
    [Fact]
    public void Degenerate_windows_have_no_p_value()
    {
        ModelHealth.TwoProportionPValue(Window(1000, 0), Window(1000, 0)).Should().BeNull();
        ModelHealth.TwoProportionPValue(Window(1000, 1000), Window(1000, 1000)).Should().BeNull();
        ModelHealth.TwoProportionPValue(Window(0, 0), Window(1000, 10)).Should().BeNull();
    }

    /// <summary>불량률이 0 에서 크게 오르는 경우도 잡아야 한다 — 가장 놓치면 안 되는 자리다.</summary>
    [Fact]
    public void Rise_from_zero_is_caught()
    {
        var verdict = ModelHealth.Evaluate(Window(2000, 0), Window(2000, 150));

        verdict.Signal.Should().Be(ModelHealthSignal.NgRateRose);
        verdict.SuggestRetrain.Should().BeTrue();
    }
}
