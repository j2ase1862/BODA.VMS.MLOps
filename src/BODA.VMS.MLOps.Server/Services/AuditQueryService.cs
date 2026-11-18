using BODA.VMS.MLOps.Contracts;
using BODA.VMS.MLOps.Contracts.Audit;
using BODA.VMS.MLOps.Server.Data;
using Microsoft.EntityFrameworkCore;

namespace BODA.VMS.MLOps.Server.Services;

/// <summary>
/// 감사 로그 읽기 (ML-25). 쓰기는 <see cref="AuditService"/> 가 각 작업의 트랜잭션 안에서 하고, 여기는 읽기만 한다.
/// 로그를 고치거나 지우는 길은 두지 않는다 — 감사 기록은 사람이 손댈 수 없어야 뜻이 있다.
/// </summary>
public sealed class AuditQueryService(MlopsDbContext db)
{
    public const int MaxTake = 500;

    public sealed record Query(DateTime? FromUtc, DateTime? ToUtc, string? Category, string? Action, string? Actor, int Skip, int Take);

    public async Task<AuditLogPageDto> ListAsync(Query q, CancellationToken ct)
    {
        if (q.FromUtc is { } f && q.ToUtc is { } t && f > t)
            throw ApiException.BadRequest(ErrorCodes.Validation, "기간의 시작이 끝보다 늦습니다.");

        var query = db.AuditLogs.AsNoTracking().AsQueryable();
        if (q.FromUtc is { } from) query = query.Where(a => a.At >= from);
        if (q.ToUtc is { } to) query = query.Where(a => a.At < to);
        if (!string.IsNullOrWhiteSpace(q.Category)) query = query.Where(a => a.Category == q.Category);
        if (!string.IsNullOrWhiteSpace(q.Action)) query = query.Where(a => a.Action.Contains(q.Action.Trim()));
        if (!string.IsNullOrWhiteSpace(q.Actor)) query = query.Where(a => a.Actor.Contains(q.Actor.Trim()));

        var total = await query.CountAsync(ct);
        var rows = await query.OrderByDescending(a => a.At).ThenByDescending(a => a.Id)
            .Skip(Math.Max(0, q.Skip)).Take(Math.Clamp(q.Take, 1, MaxTake))
            .Select(a => new AuditLogDto(a.Id, a.Category, a.Action, a.Actor, a.At, a.EntityId, a.DetailsJson))
            .ToListAsync(ct);
        var categories = await db.AuditLogs.AsNoTracking().Select(a => a.Category).Distinct().OrderBy(c => c).ToListAsync(ct);

        // SQLite 는 DateTime 의 종류를 남기지 않는다 — 화면이 현지 시각으로 바꿀 수 있게 UTC 로 못 박는다
        var items = rows.Select(r => r with { At = DateTime.SpecifyKind(r.At, DateTimeKind.Utc) }).ToList();
        return new AuditLogPageDto(items, total, categories);
    }
}
