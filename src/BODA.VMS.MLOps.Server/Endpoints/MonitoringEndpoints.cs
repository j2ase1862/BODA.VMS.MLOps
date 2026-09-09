using BODA.VMS.MLOps.Server.Auth;
using BODA.VMS.MLOps.Server.Services.Monitoring;

namespace BODA.VMS.MLOps.Server.Endpoints;

/// <summary>
/// 라인에 나간 모델을 지켜보는 화면이 쓰는 곳 (Phase 5 — 모니터링·재학습 루프).
/// </summary>
public static class MonitoringEndpoints
{
    public static RouteGroupBuilder MapMonitoringEndpoints(this RouteGroupBuilder api)
    {
        var g = api.MapGroup("/monitoring").WithTags("Monitoring");

        // 운영 웹에서 결과를 당겨 와 모델별 상태를 낸다. 설정이 없거나 운영 웹에 닿지 못하면
        // available=false 와 이유를 준다 — 화면이 "왜 안 보이는지" 를 말할 수 있어야 한다.
        g.MapGet("/models", async (ModelMonitorService monitor, CancellationToken ct) =>
            Results.Ok(await monitor.GetReportAsync(ct))).RequireAuthorization(Policies.Viewer);

        return g;
    }
}
