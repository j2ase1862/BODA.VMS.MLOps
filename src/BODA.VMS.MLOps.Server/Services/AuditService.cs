using System.Text.Json;
using BODA.VMS.MLOps.Contracts;
using BODA.VMS.MLOps.Server.Data;
using BODA.VMS.MLOps.Server.Data.Entities;

namespace BODA.VMS.MLOps.Server.Services;

/// <summary>감사 로그 카테고리·액션 (Phase 1 §8, Phase 3 §8). SaveChanges 는 호출 측 트랜잭션에 묶인다.</summary>
public sealed class AuditService(MlopsDbContext db, TimeProvider clock)
{
    public const string Model = "Model";
    public const string Dataset = "Dataset";
    public const string Training = "Training";
    public const string Worker = "Worker";

    public void Record(string category, string action, string actor, string? entityId, object? details = null)
    {
        db.AuditLogs.Add(new AuditLog
        {
            Category = category,
            Action = action,
            Actor = actor,
            At = clock.GetUtcNow().UtcDateTime,
            EntityId = entityId,
            DetailsJson = details is null ? null : JsonSerializer.Serialize(details, MlopsJson.Options),
        });
    }
}
