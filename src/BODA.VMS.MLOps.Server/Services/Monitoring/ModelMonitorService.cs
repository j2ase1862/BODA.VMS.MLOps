using BODA.VMS.MLOps.Contracts.Models;
using BODA.VMS.MLOps.Core.Monitoring;
using BODA.VMS.MLOps.Server.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace BODA.VMS.MLOps.Server.Services.Monitoring;

/// <summary>
/// 라인에 나간 모델이 실제로 어떻게 하고 있는지 본다 (Phase 5 — 모니터링·재학습 루프).
///
/// <para>
/// 운영 웹에서 모델별 검사 결과를 당겨 와, 기준 구간과 최근 구간을 견준다.
/// 판단 규칙은 <see cref="ModelHealth"/> 한 곳에 있고 여기는 데이터를 모아 주는 일만 한다.
/// </para>
/// <para><b>못 당겨 와도 죽지 않는다.</b>
/// 운영 웹이 없거나(주소 미설정), 아직 이 API 가 없는 판이거나(v1.8.0 에는 없다), 잠깐 죽었어도
/// 이 기능만 "볼 수 없음" 으로 남고 MLOps 의 나머지는 그대로 돈다.
/// </para>
/// </summary>
public sealed class ModelMonitorService(
    MlopsDbContext db,
    ProductionOutcomeClient client,
    IOptions<MonitoringOptions> options,
    TimeProvider clock,
    ILogger<ModelMonitorService> logger)
{
    private readonly MonitoringOptions _o = options.Value;

    /// <summary>설정이 없으면 이 기능은 꺼진 것이다.</summary>
    public bool IsConfigured => !string.IsNullOrWhiteSpace(_o.ProductionWebUrl);

    /// <summary>
    /// 지금 라인에 나가 있는 모델들의 상태.
    ///
    /// <para>
    /// 운영 웹이 <c>mv:{버전 id}</c> 로 알려 준 것만 우리 모델 버전과 이어진다. 옛 형식이나
    /// DL 을 안 쓴 검사도 버리지 않고 "이어지지 않은 것" 으로 함께 낸다 — 그 수가 크면
    /// 라인이 아직 옛 VMS 를 쓰고 있다는 뜻이라 그 자체가 알아야 할 정보다.
    /// </para>
    /// </summary>
    public async Task<ModelMonitorReportDto> GetReportAsync(CancellationToken ct)
    {
        if (!IsConfigured)
        {
            return new ModelMonitorReportDto(
                Available: false,
                Message: "운영 웹 주소가 설정되지 않았습니다 (Monitoring:ProductionWebUrl). 모니터링이 꺼져 있습니다.",
                GeneratedAt: clock.GetUtcNow().UtcDateTime, Models: [], UnmatchedTotal: 0);
        }

        var now = clock.GetUtcNow().UtcDateTime;
        var recentStart = now.AddDays(-_o.RecentWindowDays);
        var baselineStart = recentStart.AddDays(-_o.BaselineWindowDays);

        IReadOnlyList<ProductionOutcome> baseline, recent;
        try
        {
            // endDate 는 포함 비교(<=)라 그 시각까지만 들어온다. 경계에서 몇 건 빠지지 않도록
            // 구간의 끝을 그대로 넘긴다 — 두 구간이 맞닿아 있어 빈틈이 생기지 않는다.
            baseline = await client.GetAsync(_o.ProductionWebUrl, baselineStart, recentStart, _o.ClientId, daily: false, ct);
            recent = await client.GetAsync(_o.ProductionWebUrl, recentStart, now, _o.ClientId, daily: false, ct);
        }
        catch (ProductionOutcomeException ex)
        {
            logger.LogWarning("운영 결과를 당겨 오지 못했습니다: {Message}", ex.Message);
            return new ModelMonitorReportDto(
                Available: false,
                Message: $"운영 웹에서 검사 결과를 받지 못했습니다 — {ex.Message}",
                GeneratedAt: now, Models: [], UnmatchedTotal: 0);
        }

        var baselineByVersion = ByVersionId(baseline);
        var recentByVersion = ByVersionId(recent);

        // 우리가 아는 모델 버전만 이름을 붙일 수 있다
        var versionIds = baselineByVersion.Keys.Union(recentByVersion.Keys).ToList();
        var versions = await db.ModelVersions.AsNoTracking()
            .Where(v => versionIds.Contains(v.Id))
            .Join(db.Models.AsNoTracking(), v => v.ModelId, m => m.Id,
                (v, m) => new { v.Id, v.ModelId, v.Number, v.Stage, v.TrainingJobId, ModelName = m.Name, m.TaskType })
            .ToListAsync(ct);

        var models = new List<ModelHealthDto>();
        foreach (var version in versions.OrderBy(v => v.ModelName).ThenByDescending(v => v.Number))
        {
            var b = baselineByVersion.GetValueOrDefault(version.Id);
            var r = recentByVersion.GetValueOrDefault(version.Id);
            var verdict = ModelHealth.Evaluate(b, r);

            models.Add(new ModelHealthDto(
                version.Id, version.ModelId, version.ModelName, version.Number, version.Stage, version.TaskType,
                verdict.Signal.ToString(), verdict.Detail,
                b.Total, b.NgRate, r.Total, r.NgRate, verdict.PValue,
                r.AvgConfidence, r.ConfidenceSamples, verdict.SuggestRetrain, version.TrainingJobId));
        }

        // 이어지지 않은 검사 — 옛 VMS 이거나 DL 을 안 쓴 스텝
        int unmatched = recent
            .Where(o => Core.Monitoring.ModelVersionTag.TryRead(o.ModelVersion) is null)
            .Sum(o => o.TotalCount);

        return new ModelMonitorReportDto(
            Available: true,
            Message: models.Count == 0
                ? "아직 이어진 검사 결과가 없습니다. 라인이 model:// 참조를 쓰는 레시피로 돌면 여기에 나타납니다."
                : $"모델 {models.Count}개를 보고 있습니다.",
            GeneratedAt: now, Models: models, UnmatchedTotal: unmatched);
    }

    /// <summary>같은 버전 id 로 온 줄을 합친다. 라인이 여럿이면 여러 줄로 올 수 있다.</summary>
    private static Dictionary<Guid, OutcomeWindow> ByVersionId(IReadOnlyList<ProductionOutcome> outcomes)
    {
        var result = new Dictionary<Guid, OutcomeWindow>();
        foreach (var group in outcomes
                     .Select(o => (Id: Core.Monitoring.ModelVersionTag.TryRead(o.ModelVersion), Outcome: o))
                     .Where(x => x.Id is not null)
                     .GroupBy(x => x.Id!.Value))
        {
            int total = group.Sum(x => x.Outcome.TotalCount);
            int ng = group.Sum(x => x.Outcome.NgCount);
            int confidenceSamples = group.Sum(x => x.Outcome.ConfidenceSampleCount);

            result[group.Key] = new OutcomeWindow(
                total, ng,
                AvgConfidence: WeightedAverage(group.Select(x => (x.Outcome.AvgConfidence, x.Outcome.ConfidenceSampleCount))),
                ConfidenceSamples: confidenceSamples,
                AvgBrightness: WeightedAverage(group.Select(x => (x.Outcome.AvgBrightness, x.Outcome.TotalCount))),
                AvgFocusScore: WeightedAverage(group.Select(x => (x.Outcome.AvgFocusScore, x.Outcome.TotalCount))));
        }
        return result;
    }

    /// <summary>
    /// 건수로 가중한 평균. 그냥 평균 내면 10건짜리 라인과 10000건짜리 라인이 같은 무게가 되어
    /// 작은 라인 하나가 전체 판단을 흔든다.
    /// </summary>
    private static double? WeightedAverage(IEnumerable<(double? Value, int Weight)> items)
    {
        double sum = 0;
        long weight = 0;
        foreach (var (value, w) in items)
        {
            if (value is not { } v || w <= 0) continue;
            sum += v * w;
            weight += w;
        }
        return weight > 0 ? sum / weight : null;
    }
}
