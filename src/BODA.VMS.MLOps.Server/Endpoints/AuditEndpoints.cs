using BODA.VMS.MLOps.Server.Auth;
using BODA.VMS.MLOps.Server.Services;

namespace BODA.VMS.MLOps.Server.Endpoints;

/// <summary>감사 로그 읽기 (ML-25) — Admin 만. 쓰기·지우기 길은 없다.</summary>
public static class AuditEndpoints
{
    public static RouteGroupBuilder MapAuditEndpoints(this RouteGroupBuilder api)
    {
        var g = api.MapGroup("/audit").WithTags("Audit");

        // from·to 는 UTC. to 는 그 시각을 넣지 않는다(반열린 구간) — 하루 단위로 끊을 때 경계가 겹치지 않는다.
        g.MapGet("/", async (DateTime? from, DateTime? to, string? category, string? action, string? actor,
                int? skip, int? take, AuditQueryService svc, CancellationToken ct) =>
            Results.Ok(await svc.ListAsync(new AuditQueryService.Query(
                ToUtc(from), ToUtc(to), category, action, actor, skip ?? 0, take ?? 100), ct)))
            .RequireAuthorization(Policies.Admin);

        return api;
    }

    private static DateTime? ToUtc(DateTime? value) => value switch
    {
        null => null,
        { Kind: DateTimeKind.Local } v => v.ToUniversalTime(),
        { Kind: DateTimeKind.Unspecified } v => DateTime.SpecifyKind(v, DateTimeKind.Utc),
        { } v => v,
    };
}
