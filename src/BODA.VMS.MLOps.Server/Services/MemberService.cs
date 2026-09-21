using BODA.VMS.MLOps.Contracts;
using BODA.VMS.MLOps.Contracts.Members;
using BODA.VMS.MLOps.Server.Auth;
using BODA.VMS.MLOps.Server.Data;
using BODA.VMS.MLOps.Server.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

namespace BODA.VMS.MLOps.Server.Services;

/// <summary>
/// 운영 웹 계정 ↔ MLOps 역할 표 (개발 문서 §6).
/// 인증은 운영 웹이 하고 인가는 여기가 정한다 — 이유는 <see cref="Member"/> 주석에 있다.
/// </summary>
public sealed class MemberService(
    MlopsDbContext db,
    AuditService audit,
    IMemoryCache cache,
    IOptions<AuthOptions> auth,
    TimeProvider clock)
{
    private DateTime Now => clock.GetUtcNow().UtcDateTime;

    /// <summary>줄 수 있는 역할 — 사다리 순서대로. 서비스 계정 역할(Worker·Line)은 사람에게 주지 않는다.</summary>
    public static readonly string[] Assignable = [Roles.Viewer, Roles.Labeler, Roles.Engineer, Roles.Admin];

    public MemberPolicyDto Policy() =>
        new(auth.Value.DefaultRole ?? "", auth.Value.BootstrapWebRole ?? "", Assignable);

    public async Task<List<MemberDto>> ListAsync(CancellationToken ct) =>
        (await db.Members.AsNoTracking().OrderBy(m => m.Username).ToListAsync(ct)).Select(ToDto).ToList();

    public async Task<MemberDto> GrantAsync(GrantMemberRequest req, CurrentUser user, CancellationToken ct)
    {
        var username = Member.Normalize(req.Username ?? "");
        if (username.Length == 0) throw ApiException.BadRequest(ErrorCodes.Validation, "계정 이름이 필요합니다.");

        var role = Assignable.FirstOrDefault(r => r.Equals(req.Role, StringComparison.OrdinalIgnoreCase))
                   ?? throw ApiException.BadRequest(ErrorCodes.Validation,
                       $"역할은 {string.Join(" · ", Assignable)} 중 하나여야 합니다.");

        var entity = await db.Members.FirstOrDefaultAsync(m => m.Username == username, ct);
        var before = entity?.Role;
        if (entity is null)
        {
            entity = new Member { Id = Guid.NewGuid(), Username = username };
            db.Members.Add(entity);
        }

        entity.Role = role;
        entity.DisplayName = string.IsNullOrWhiteSpace(req.DisplayName) ? entity.DisplayName : req.DisplayName.Trim();
        entity.Note = string.IsNullOrWhiteSpace(req.Note) ? null : req.Note.Trim();
        entity.GrantedBy = user.Name;
        entity.GrantedAt = Now;

        audit.Record(AuditService.Model, "MemberRoleGranted", user.Name, username, new { before, after = role });
        await db.SaveChangesAsync(ct);

        // 다음 요청부터 바로 듣게 한다. 권한을 낮춘 조치가 캐시 때문에 늦으면 안 된다.
        MemberRoleClaimsTransformation.Invalidate(cache, username);
        return ToDto(entity);
    }

    public async Task RevokeAsync(Guid id, CurrentUser user, CancellationToken ct)
    {
        var entity = await db.Members.FirstOrDefaultAsync(m => m.Id == id, ct)
                     ?? throw ApiException.NotFound("역할 표의 그 계정");

        db.Members.Remove(entity);
        audit.Record(AuditService.Model, "MemberRoleRevoked", user.Name, entity.Username, new { entity.Role });
        await db.SaveChangesAsync(ct);

        MemberRoleClaimsTransformation.Invalidate(cache, entity.Username);
    }

    /// <summary>
    /// 로그인 한 번에 한 번만 불린다(<c>/api/auth/me</c>). 표에 있는 사람이면 마지막 접속 시각과
    /// 표시 이름을 갱신한다 — 없는 사람에게 줄을 만들지는 않는다. 그건 관리자가 하는 일이다.
    /// </summary>
    public async Task TouchAsync(string username, string? displayName, CancellationToken ct)
    {
        var normalized = Member.Normalize(username ?? "");
        if (normalized.Length == 0) return;

        var entity = await db.Members.FirstOrDefaultAsync(m => m.Username == normalized, ct);
        if (entity is null) return;

        entity.LastSeenAt = Now;
        if (!string.IsNullOrWhiteSpace(displayName)) entity.DisplayName = displayName.Trim();
        await db.SaveChangesAsync(ct);
    }

    private static MemberDto ToDto(Member m) =>
        new(m.Id, m.Username, m.Role, m.DisplayName, m.Note, m.GrantedBy, m.GrantedAt, m.LastSeenAt);
}
