using System.Security.Cryptography;
using BODA.VMS.MLOps.Contracts;
using BODA.VMS.MLOps.Contracts.Lines;
using BODA.VMS.MLOps.Core.Hashing;
using BODA.VMS.MLOps.Server.Auth;
using BODA.VMS.MLOps.Server.Data;
using BODA.VMS.MLOps.Server.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace BODA.VMS.MLOps.Server.Services;

/// <summary>
/// 라인 PC 서비스 계정 (개발 문서 §5.1 배포 · §5.2 수집).
/// 워커 계정과 같은 규칙이다 — 토큰 원문은 발급 응답에서 한 번만 나가고 서버에는 SHA-256 해시만 남는다.
/// </summary>
public sealed class LineClientService(MlopsDbContext db, AuditService audit, TimeProvider clock)
{
    private DateTime Now => clock.GetUtcNow().UtcDateTime;

    /// <summary>워커 토큰과 같은 방식 — 접두사로 스킴을 구분하므로 접두사가 규약의 일부다.</summary>
    public static string NewToken()
    {
        var bytes = RandomNumberGenerator.GetBytes(32);
        return LineTokenAuthenticationHandler.TokenPrefix
               + Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    public async Task<List<LineClientDto>> ListAsync(CancellationToken ct) =>
        (await db.LineClients.AsNoTracking().OrderBy(c => c.LineId).ThenBy(c => c.Name).ToListAsync(ct))
        .Select(ToDto).ToList();

    public async Task<CreateLineClientResponse> CreateAsync(CreateLineClientRequest req, CurrentUser user, CancellationToken ct)
    {
        var name = (req.Name ?? "").Trim();
        var lineId = (req.LineId ?? "").Trim();
        if (name.Length == 0) throw ApiException.BadRequest(ErrorCodes.Validation, "이름이 필요합니다.");
        if (lineId.Length == 0) throw ApiException.BadRequest(ErrorCodes.Validation, "lineId 가 필요합니다.");

        var token = NewToken();
        var entity = new LineClient
        {
            Id = Guid.NewGuid(),
            Name = name,
            LineId = lineId,
            TokenHash = Sha256Util.HashString(token),
            CreatedBy = user.Name,
            CreatedAt = Now,
        };
        db.LineClients.Add(entity);
        audit.Record(AuditService.Model, "LineClientCreated", user.Name, entity.Id.ToString(), new { entity.Name, entity.LineId });
        await db.SaveChangesAsync(ct);
        return new CreateLineClientResponse(ToDto(entity), token);
    }

    public async Task<CreateLineClientResponse> RotateTokenAsync(Guid id, CurrentUser user, CancellationToken ct)
    {
        var entity = await FindAsync(id, ct);
        var token = NewToken();
        entity.TokenHash = Sha256Util.HashString(token);
        audit.Record(AuditService.Model, "LineClientTokenRotated", user.Name, id.ToString(), new { entity.LineId });
        await db.SaveChangesAsync(ct);
        return new CreateLineClientResponse(ToDto(entity), token);
    }

    public async Task<LineClientDto> SetDisabledAsync(Guid id, bool disabled, string? reason, CurrentUser user, CancellationToken ct)
    {
        var entity = await FindAsync(id, ct);
        entity.Disabled = disabled;
        entity.DisabledReason = disabled ? reason : null;
        audit.Record(AuditService.Model, disabled ? "LineClientDisabled" : "LineClientEnabled",
            user.Name, id.ToString(), new { entity.LineId, reason });
        await db.SaveChangesAsync(ct);
        return ToDto(entity);
    }

    public async Task DeleteAsync(Guid id, CurrentUser user, CancellationToken ct)
    {
        var entity = await FindAsync(id, ct);
        db.LineClients.Remove(entity);
        audit.Record(AuditService.Model, "LineClientDeleted", user.Name, id.ToString(), new { entity.Name, entity.LineId });
        await db.SaveChangesAsync(ct);
    }

    private async Task<LineClient> FindAsync(Guid id, CancellationToken ct) =>
        await db.LineClients.FirstOrDefaultAsync(c => c.Id == id, ct)
        ?? throw ApiException.NotFound("라인 계정");

    private static LineClientDto ToDto(LineClient c) =>
        new(c.Id, c.Name, c.LineId, c.Disabled, c.DisabledReason, c.LastSeenAt, c.CreatedBy, c.CreatedAt);
}
