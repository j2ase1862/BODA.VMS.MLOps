namespace BODA.VMS.MLOps.Contracts.Audit;

/// <summary>감사 로그 한 줄 (ML-25). 시각은 UTC.</summary>
public sealed record AuditLogDto(long Id, string Category, string Action, string Actor, DateTime At, string? EntityId, string? Details);

/// <summary>감사 로그 한 쪽. <c>Categories</c> 는 거르기 목록에 쓸 종류 전부(쪽과 무관).</summary>
public sealed record AuditLogPageDto(IReadOnlyList<AuditLogDto> Items, int Total, IReadOnlyList<string> Categories);
