namespace BODA.VMS.MLOps.Core.Monitoring;

/// <summary>한 구간의 관측치 — 라인에서 이 모델이 실제로 낸 결과.</summary>
/// <param name="Total">검사 건수</param>
/// <param name="Ng">그중 불량 판정</param>
/// <param name="AvgConfidence">DL 신뢰도 평균. 보고한 건이 없으면 null.</param>
/// <param name="ConfidenceSamples">신뢰도를 실제로 보고한 건수</param>
/// <param name="AvgBrightness">입력 이미지 평균 밝기</param>
/// <param name="AvgFocusScore">입력 이미지 선명도 평균</param>
public readonly record struct OutcomeWindow(
    int Total,
    int Ng,
    double? AvgConfidence = null,
    int ConfidenceSamples = 0,
    double? AvgBrightness = null,
    double? AvgFocusScore = null)
{
    /// <summary>불량률 0~1. 0건이면 0.</summary>
    public double NgRate => Total > 0 ? (double)Ng / Total : 0;
}

/// <summary>무엇이 달라졌다고 보는가.</summary>
public enum ModelHealthSignal
{
    /// <summary>기준과 견줘 달라진 것이 없다.</summary>
    Steady,

    /// <summary>표본이 모자라 판단하지 않는다. "괜찮다" 가 아니라 "아직 모른다" 이다.</summary>
    NotEnoughData,

    /// <summary>불량률이 올랐다.</summary>
    NgRateRose,

    /// <summary>신뢰도가 내려갔다. 불량률이 아직 안 움직였어도 앞선 신호일 수 있다.</summary>
    ConfidenceDropped,

    /// <summary>입력이 달라졌다 — 조명·초점·렌즈를 먼저 본다. 모델을 다시 학습해도 안 낫는다.</summary>
    InputDrifted,
}

/// <param name="Signal">무엇이 달라졌는가</param>
/// <param name="Detail">사람에게 보여 줄 한 줄</param>
/// <param name="BaselineNgRate">기준 구간 불량률 0~1</param>
/// <param name="RecentNgRate">최근 구간 불량률 0~1</param>
/// <param name="PValue">두 불량률이 우연히 이만큼 벌어질 확률. 낮을수록 우연이 아니다. 잴 수 없으면 null.</param>
/// <param name="SuggestRetrain">재학습을 권할 만한가</param>
public readonly record struct ModelHealthVerdict(
    ModelHealthSignal Signal,
    string Detail,
    double BaselineNgRate,
    double RecentNgRate,
    double? PValue,
    bool SuggestRetrain);

/// <summary>
/// 라인에 나간 모델이 나빠졌는지 본다 (개발 문서 Phase 5 — 모니터링·재학습 루프).
///
/// <para><b>왜 통계를 쓰는가.</b>
/// "불량률이 5% 올랐다" 만으로 경고하면 표본이 적을 때 계속 울린다. 20건 중 1건이 2건이 되면
/// 5%p 상승이지만 그건 그냥 흔들림이다. 몇 번 헛경고가 나면 사람은 경고를 통째로 무시하고,
/// 그때부터 진짜 문제도 지나친다. 그래서 <b>두 비율의 차이가 우연으로 설명되는지</b> 를 함께 본다.
/// </para>
/// <para><b>무엇을 못 하는가.</b>
/// 이것은 원인을 말해 주지 않는다. 불량률이 올랐다는 것은 모델이 나빠졌다는 뜻일 수도,
/// 실제로 불량이 늘었다는 뜻일 수도 있다 — 후자라면 모델은 제 일을 한 것이다.
/// 그래서 입력 특성(밝기·선명도)을 함께 보고, 그쪽이 움직였으면 <see cref="ModelHealthSignal.InputDrifted"/>
/// 로 먼저 알린다. 최종 판단은 사람이 한다. 이 값으로 모델을 자동으로 내리지 않는다.
/// </para>
/// </summary>
public static class ModelHealth
{
    /// <summary>이보다 적으면 판단하지 않는다. 두 구간 모두 이 수를 넘어야 한다.</summary>
    public const int MinSamples = 100;

    /// <summary>이 확률보다 드물게 일어날 차이면 우연으로 보지 않는다.</summary>
    public const double SignificanceLevel = 0.01;

    /// <summary>이만큼은 올라야 사람이 볼 가치가 있다 (통계적으로 유의해도 0.5%p 상승은 실무에서 의미가 없다).</summary>
    public const double MinNgRateRise = 0.02;

    /// <summary>신뢰도가 이 비율만큼 내려가면 알린다 (기준 대비).</summary>
    public const double ConfidenceDropRatio = 0.10;

    /// <summary>입력 특성이 기준 대비 이 비율만큼 움직이면 알린다.</summary>
    public const double InputDriftRatio = 0.20;

    /// <summary>
    /// 기준 구간과 최근 구간을 견준다.
    ///
    /// <para>
    /// 순서가 있다. 입력이 달라졌으면 그것부터 말한다 — 모델을 다시 학습해도 낫지 않기 때문이다.
    /// 그다음이 불량률, 그다음이 신뢰도다. 신뢰도는 불량률보다 먼저 움직이는 경우가 있어
    /// 마지막에 남겨 두고 본다.
    /// </para>
    /// </summary>
    public static ModelHealthVerdict Evaluate(OutcomeWindow baseline, OutcomeWindow recent)
    {
        if (baseline.Total < MinSamples || recent.Total < MinSamples)
        {
            return new ModelHealthVerdict(
                ModelHealthSignal.NotEnoughData,
                $"표본이 모자랍니다 (기준 {baseline.Total}건 · 최근 {recent.Total}건, {MinSamples}건 이상 필요). " +
                "괜찮다는 뜻이 아니라 아직 판단할 수 없다는 뜻입니다.",
                baseline.NgRate, recent.NgRate, null, SuggestRetrain: false);
        }

        // 1) 입력이 달라졌는가 — 모델을 다시 학습해도 낫지 않는 종류다
        if (DescribeInputDrift(baseline, recent) is { } drift)
        {
            return new ModelHealthVerdict(
                ModelHealthSignal.InputDrifted, drift,
                baseline.NgRate, recent.NgRate, null, SuggestRetrain: false);
        }

        // 2) 불량률이 올랐는가
        double rise = recent.NgRate - baseline.NgRate;
        double? p = TwoProportionPValue(baseline, recent);

        if (rise >= MinNgRateRise && p is { } value && value < SignificanceLevel)
        {
            return new ModelHealthVerdict(
                ModelHealthSignal.NgRateRose,
                $"불량률이 {baseline.NgRate:P1} → {recent.NgRate:P1} 로 올랐습니다 " +
                $"(+{rise:P1}, 우연일 확률 {FormatPValue(value)}). 모델이 나빠졌는지, 실제로 불량이 는 것인지 " +
                "라인에서 확인하세요.",
                baseline.NgRate, recent.NgRate, p, SuggestRetrain: true);
        }

        // 3) 신뢰도가 내려갔는가 — 불량률보다 먼저 움직이기도 한다
        if (DescribeConfidenceDrop(baseline, recent) is { } drop)
        {
            return new ModelHealthVerdict(
                ModelHealthSignal.ConfidenceDropped, drop,
                baseline.NgRate, recent.NgRate, p, SuggestRetrain: true);
        }

        return new ModelHealthVerdict(
            ModelHealthSignal.Steady,
            $"불량률 {recent.NgRate:P1} — 기준({baseline.NgRate:P1})과 견줘 달라진 것이 없습니다.",
            baseline.NgRate, recent.NgRate, p, SuggestRetrain: false);
    }

    /// <summary>
    /// 사람이 읽을 확률. 아주 작은 값을 그대로 <c>P2</c> 로 찍으면 "0.00%" 가 되어
    /// 계산이 안 된 것처럼 보인다 — 정규 근사가 꼬리에서 0 으로 내려앉기도 한다.
    /// 그 구간은 "0.01% 미만" 으로 말한다.
    /// </summary>
    private static string FormatPValue(double p) => p < 0.0001 ? "0.01% 미만" : $"{p:P2}";

    private static string? DescribeInputDrift(OutcomeWindow baseline, OutcomeWindow recent)
    {
        if (Moved(baseline.AvgBrightness, recent.AvgBrightness) is { } b)
            return $"입력 이미지의 밝기가 {baseline.AvgBrightness:F0} → {recent.AvgBrightness:F0} 로 {b} 조명을 먼저 확인하세요.";

        if (Moved(baseline.AvgFocusScore, recent.AvgFocusScore) is { } f)
            return $"입력 이미지의 선명도가 {baseline.AvgFocusScore:F1} → {recent.AvgFocusScore:F1} 로 {f} 초점·렌즈를 먼저 확인하세요.";

        return null;

        static string? Moved(double? before, double? after)
        {
            if (before is not { } b || after is not { } a || b <= 0) return null;
            double change = (a - b) / b;
            if (Math.Abs(change) < InputDriftRatio) return null;
            return change > 0 ? $"{change:P0} 올랐습니다 —" : $"{-change:P0} 내려갔습니다 —";
        }
    }

    private static string? DescribeConfidenceDrop(OutcomeWindow baseline, OutcomeWindow recent)
    {
        // 표본이 적은 평균은 믿을 수 없다. 신뢰도를 실제로 보고한 건수로 따로 본다 —
        // 검사는 많았는데 신뢰도는 두 건만 보고했다면 그 평균으로 모델을 판단하면 안 된다.
        if (baseline.ConfidenceSamples < MinSamples || recent.ConfidenceSamples < MinSamples) return null;
        if (baseline.AvgConfidence is not { } before || recent.AvgConfidence is not { } after) return null;
        if (before <= 0) return null;

        double drop = (before - after) / before;
        if (drop < ConfidenceDropRatio) return null;

        return $"DL 신뢰도 평균이 {before:F3} → {after:F3} 로 {drop:P0} 내려갔습니다. " +
               "불량률이 아직 안 움직였어도 앞선 신호일 수 있습니다.";
    }

    /// <summary>
    /// 두 비율이 같다고 볼 때, 관측된 만큼(또는 그보다 크게) 벌어질 확률.
    ///
    /// <para>
    /// 표준 두 비율 z 검정이다. 양쪽 꼬리를 본다 — 내려간 것도 알아야 하기 때문이다
    /// (불량률이 갑자기 0 이 되는 것도 정상은 아니다).
    /// 합동 비율이 0 이거나 1 이면 분모가 0 이라 잴 수 없고, 그때는 null 이다.
    /// </para>
    /// </summary>
    public static double? TwoProportionPValue(OutcomeWindow a, OutcomeWindow b)
    {
        if (a.Total <= 0 || b.Total <= 0) return null;

        double pooled = (double)(a.Ng + b.Ng) / (a.Total + b.Total);
        if (pooled is <= 0 or >= 1) return null;

        double se = Math.Sqrt(pooled * (1 - pooled) * (1.0 / a.Total + 1.0 / b.Total));
        if (se <= 0) return null;

        double z = (b.NgRate - a.NgRate) / se;
        return 2 * (1 - StandardNormalCdf(Math.Abs(z)));
    }

    /// <summary>
    /// 표준정규 누적분포 Φ(x). Abramowitz &amp; Stegun 26.2.17 — 절대오차 7.5e-8 이라
    /// 경고를 낼지 말지 가르는 데는 충분하다. 통계 라이브러리를 끌어오지 않으려는 것이다.
    ///
    /// <para>
    /// 이 식은 Φ 를 <b>직접</b> 근사한다. erf 식과 섞으면 안 된다 —
    /// 정규분포 밀도 계수(1/√2π)를 빠뜨리면 x=0 에서 0.5 대신 1.2533 이 나오고,
    /// 그러면 확률 이 1 을 넘는다.
    /// </para>
    /// </summary>
    private static double StandardNormalCdf(double x)
    {
        const double b1 = 0.319381530, b2 = -0.356563782, b3 = 1.781477937;
        const double b4 = -1.821255978, b5 = 1.330274429, p = 0.2316419;

        double abs = Math.Abs(x);
        double t = 1.0 / (1.0 + p * abs);
        double poly = t * (b1 + t * (b2 + t * (b3 + t * (b4 + t * b5))));
        double density = Math.Exp(-abs * abs / 2.0) / Math.Sqrt(2.0 * Math.PI);

        double upperTail = density * poly;              // ≈ 1 − Φ(|x|)
        return x >= 0 ? 1.0 - upperTail : upperTail;
    }
}
