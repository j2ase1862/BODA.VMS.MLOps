using System.Security.Cryptography;
using BODA.VMS.MLOps.Contracts;
using BODA.VMS.MLOps.Contracts.Auth;
using BODA.VMS.MLOps.Server.Auth;
using BODA.VMS.MLOps.Server.Data;
using BODA.VMS.MLOps.Server.Data.Entities;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

namespace BODA.VMS.MLOps.Server.Services;

/// <summary>
/// 자체 계정 로그인 (<see cref="AuthMode.Local"/>) — 설계 메모 "로컬 계정 로그인" 참조.
///
/// <para>
/// 운영 웹이 있는 설치에서는 이 서비스가 쓰이지 않는다. 운영 웹이 없는 곳에서 사람이 들어올
/// 유일한 문이다. 비밀번호를 우리가 보관하게 되므로 해시·잠금·감사를 여기 한곳에 모은다.
/// </para>
/// <para>
/// <b>해시는 직접 만들지 않는다.</b> <see cref="PasswordHasher{TUser}"/>(PBKDF2)를 쓴다 —
/// 반복 횟수와 솔트, 형식 업그레이드 규칙이 이미 들어 있다.
/// </para>
/// </summary>
public sealed class LocalAccountService(
    MlopsDbContext db,
    AuditService audit,
    IMemoryCache cache,
    LocalTokenIssuer issuer,
    IOptions<AuthOptions> auth,
    TimeProvider clock,
    ILogger<LocalAccountService> log)
{
    /// <summary>감사 로그 카테고리 — 계정과 로그인에 관한 일.</summary>
    public const string AuditCategory = "Account";

    /// <summary>임시 비밀번호에 쓰는 글자. 사람이 받아 적을 수 있게 0·O·1·l·I 를 뺐다.</summary>
    private const string TempAlphabet = "abcdefghijkmnpqrstuvwxyzABCDEFGHJKLMNPQRSTUVWXYZ23456789";

    private static readonly PasswordHasher<Member> Hasher = new();

    private LocalAuthOptions Options => auth.Value.Local;
    private DateTime Now => clock.GetUtcNow().UtcDateTime;

    public bool Enabled => auth.Value.Mode == AuthMode.Local;

    /// <summary>
    /// 아이디·비밀번호를 확인하고 토큰을 만든다.
    ///
    /// <para>
    /// <b>실패 이유를 나누어 알리지 않는다.</b> "그런 아이디는 없습니다" 는 계정 이름을 훑는 사람에게
    /// 절반을 알려 주는 셈이다. 잠금과 IP 제한만 따로 말한다 — 그건 정상 사용자가 스스로 풀 수 없어
    /// 이유를 알아야 한다.
    /// </para>
    /// </summary>
    public async Task<LocalLoginResponse> LoginAsync(LocalLoginRequest req, string? clientIp, CancellationToken ct)
    {
        ThrottleByIp(clientIp);

        var username = Member.Normalize(req.Username ?? "");
        var password = req.Password ?? "";
        if (username.Length == 0 || password.Length == 0)
            throw ApiException.Unauthorized("아이디 또는 비밀번호가 맞지 않습니다.");

        var member = await db.Members.FirstOrDefaultAsync(m => m.Username == username, ct);

        // 계정이 없거나 비밀번호가 없는 줄에서도 해시를 한 번 태워 같은 시간이 걸리게 한다 —
        // 응답이 유독 빨리 오는 것만으로 "그 아이디는 없다" 가 새어 나간다.
        if (member is null || string.IsNullOrEmpty(member.PasswordHash))
        {
            Hasher.VerifyHashedPassword(new Member(), DummyHash.Value, password);
            audit.Record(AuditCategory, "LocalLoginFailed", username, username, new { reason = "noAccount" });
            await db.SaveChangesAsync(ct);
            throw ApiException.Unauthorized("아이디 또는 비밀번호가 맞지 않습니다.");
        }

        if (member.LockedUntil is { } until && until > Now)
            throw ApiException.TooManyRequests(ErrorCodes.AccountLocked,
                $"연속 실패로 잠긴 계정입니다. {(until - Now).TotalMinutes:F0}분 뒤에 다시 해 보세요.");

        // 비활성은 역할 변환에서도 막지만, 토큰을 아예 내주지 않는 편이 낫다.
        if (member.Disabled)
        {
            audit.Record(AuditCategory, "LocalLoginFailed", username, username, new { reason = "disabled" });
            await db.SaveChangesAsync(ct);
            throw ApiException.Unauthorized("아이디 또는 비밀번호가 맞지 않습니다.");
        }

        var verdict = Hasher.VerifyHashedPassword(member, member.PasswordHash, password);
        if (verdict == PasswordVerificationResult.Failed)
        {
            member.FailedCount++;
            var locked = member.FailedCount >= Math.Max(1, Options.MaxFailedAttempts);
            if (locked)
            {
                member.LockedUntil = Now.AddMinutes(Math.Max(1, Options.LockoutMinutes));
                member.FailedCount = 0;
            }
            audit.Record(AuditCategory, "LocalLoginFailed", username, username,
                new { reason = "badPassword", locked });
            await db.SaveChangesAsync(ct);

            if (locked)
                throw ApiException.TooManyRequests(ErrorCodes.AccountLocked,
                    $"연속 실패로 계정을 {Options.LockoutMinutes}분 동안 잠갔습니다.");
            throw ApiException.Unauthorized("아이디 또는 비밀번호가 맞지 않습니다.");
        }

        // 해시 형식이 옛것이면 이 기회에 새로 쓴다 (판단은 PasswordHasher 가 한다).
        if (verdict == PasswordVerificationResult.SuccessRehashNeeded)
            member.PasswordHash = Hasher.HashPassword(member, password);

        member.FailedCount = 0;
        member.LockedUntil = null;
        member.LastSeenAt = Now;
        audit.Record(AuditCategory, "LocalLoginSucceeded", username, username, null);
        await db.SaveChangesAsync(ct);

        MemberRoleClaimsTransformation.Invalidate(cache, username);
        return new LocalLoginResponse(issuer.Issue(member), member.Username, member.DisplayName,
            member.Role, member.MustChangePassword);
    }

    /// <summary>
    /// 본인이 비밀번호를 바꾼다. 임시 비밀번호 상태를 푸는 통로이기도 하다.
    /// 바꾸면 <see cref="Member.SecurityStamp"/> 가 새로 찍혀 <b>다른 자리에 남아 있던 세션이 즉시 끊긴다</b>.
    /// </summary>
    public async Task<LocalLoginResponse> ChangePasswordAsync(string username, ChangePasswordRequest req, CancellationToken ct)
    {
        var normalized = Member.Normalize(username ?? "");
        var member = await db.Members.FirstOrDefaultAsync(m => m.Username == normalized, ct)
                     ?? throw ApiException.NotFound("그 계정");

        if (string.IsNullOrEmpty(member.PasswordHash)
            || Hasher.VerifyHashedPassword(member, member.PasswordHash, req.CurrentPassword ?? "") == PasswordVerificationResult.Failed)
            throw ApiException.Unauthorized("지금 비밀번호가 맞지 않습니다.");

        SetPassword(member, Validate(req.NewPassword ?? "", req.CurrentPassword ?? ""), mustChange: false);
        audit.Record(AuditCategory, "PasswordChanged", member.Username, member.Username, null);
        await db.SaveChangesAsync(ct);

        MemberRoleClaimsTransformation.Invalidate(cache, member.Username);
        // 방금 바꾼 사람까지 쫓아내지는 않는다 — 새 도장을 찍은 토큰을 바로 돌려준다.
        return new LocalLoginResponse(issuer.Issue(member), member.Username, member.DisplayName,
            member.Role, member.MustChangePassword);
    }

    /// <summary>
    /// 관리자가 임시 비밀번호를 내준다. 비밀번호를 잊은 사람의 유일한 길이다 —
    /// 폐쇄망이라 메일로 찾는 방법이 없다.
    /// </summary>
    public async Task<TemporaryPasswordResponse> ResetPasswordAsync(Guid id, CurrentUser actor, CancellationToken ct)
    {
        var member = await db.Members.FirstOrDefaultAsync(m => m.Id == id, ct)
                     ?? throw ApiException.NotFound("역할 표의 그 계정");

        var temporary = GenerateTemporaryPassword();
        SetPassword(member, temporary, mustChange: true);
        member.FailedCount = 0;
        member.LockedUntil = null;
        audit.Record(AuditCategory, "PasswordReset", actor.Name, member.Username, null);
        await db.SaveChangesAsync(ct);

        MemberRoleClaimsTransformation.Invalidate(cache, member.Username);
        return new TemporaryPasswordResponse(member.Username, temporary);
    }

    /// <summary>계정을 쓰지 못하게 하거나 되살린다. 살아 있는 토큰도 다음 요청부터 막힌다.</summary>
    public async Task<MemberSnapshotResult> SetDisabledAsync(Guid id, bool disabled, CurrentUser actor, CancellationToken ct)
    {
        var member = await db.Members.FirstOrDefaultAsync(m => m.Id == id, ct)
                     ?? throw ApiException.NotFound("역할 표의 그 계정");

        member.Disabled = disabled;
        if (!disabled) { member.FailedCount = 0; member.LockedUntil = null; }
        audit.Record(AuditCategory, disabled ? "AccountDisabled" : "AccountEnabled", actor.Name, member.Username, null);
        await db.SaveChangesAsync(ct);

        MemberRoleClaimsTransformation.Invalidate(cache, member.Username);
        return new MemberSnapshotResult(member.Username, member.Disabled);
    }

    /// <summary>
    /// 표가 비어 있는 새 서버에 첫 관리자를 만든다. 임시 비밀번호는 설치 폴더에 한 번 남기고
    /// 로그에 그 위치를 알린다 — 사람이 그것으로 들어와 비밀번호를 바꾸고 나머지를 올린다.
    /// </summary>
    public async Task<string?> BootstrapAsync(string installDir, CancellationToken ct)
    {
        if (!Enabled) return null;
        var username = Member.Normalize(Options.BootstrapAdmin ?? "");
        if (username.Length == 0) return null;
        if (await db.Members.AnyAsync(ct)) return null;

        var temporary = GenerateTemporaryPassword();
        var admin = new Member
        {
            Id = Guid.NewGuid(),
            Username = username,
            Role = Roles.Admin,
            DisplayName = username,
            Note = "설치할 때 자동으로 만든 첫 관리자",
            GrantedBy = "system",
            GrantedAt = Now,
        };
        SetPassword(admin, temporary, mustChange: true);
        db.Members.Add(admin);
        audit.Record(AuditCategory, "BootstrapAdminCreated", "system", username, null);
        await db.SaveChangesAsync(ct);

        var path = Path.Combine(installDir, "initial-admin-password.txt");
        var text = "BODA VMS MLOps - 첫 관리자 계정\r\n"
                   + $"아이디       : {username}\r\n"
                   + $"임시 비밀번호: {temporary}\r\n\r\n"
                   + "첫 로그인에서 비밀번호를 바꿔야 합니다. 바꾼 뒤 이 파일을 지우세요.\r\n";
        try
        {
            await File.WriteAllTextAsync(path, text, ct);
            log.LogWarning("첫 관리자 계정({User})을 만들었습니다. 임시 비밀번호는 {Path} 에 있습니다.", username, path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 파일을 못 써도 계정은 만들어졌다. 로그로라도 알려야 사람이 들어올 수 있다.
            log.LogWarning(ex, "임시 비밀번호 파일을 쓰지 못했습니다 ({Path}). {User} 의 임시 비밀번호: {Password}",
                path, username, temporary);
        }
        return temporary;
    }

    /// <summary>임시 비밀번호 — 사람이 한 번 받아 적고 바로 바꿀 것이라 12자로 둔다.</summary>
    public static string GenerateTemporaryPassword(int length = 12) =>
        new(RandomNumberGenerator.GetItems<char>(TempAlphabet, length));

    private void SetPassword(Member member, string password, bool mustChange)
    {
        member.PasswordHash = Hasher.HashPassword(member, password);
        member.PasswordUpdatedAt = Now;
        // 새 세대를 찍는다 — 이 순간 다른 자리에 남아 있던 토큰이 모두 죽는다.
        member.SecurityStamp = Guid.NewGuid().ToString("N");
        member.MustChangePassword = mustChange;
    }

    private string Validate(string next, string current)
    {
        var min = Math.Max(4, Options.MinPasswordLength);
        if (next.Length < min)
            throw ApiException.BadRequest(ErrorCodes.Validation, $"새 비밀번호는 {min}자 이상이어야 합니다.");
        if (next == current)
            throw ApiException.BadRequest(ErrorCodes.Validation, "지금 쓰는 것과 다른 비밀번호여야 합니다.");
        return next;
    }

    /// <summary>
    /// 한 IP 의 시도 횟수를 1분 창으로 센다. 계정 잠금만으로는 아이디를 바꿔 가며 찌르는 것을 막지 못한다.
    /// </summary>
    private void ThrottleByIp(string? clientIp)
    {
        var limit = Options.MaxAttemptsPerIpPerMinute;
        if (limit <= 0 || string.IsNullOrWhiteSpace(clientIp)) return;

        var counter = cache.GetOrCreate("login-ip:" + clientIp, e =>
        {
            e.AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(1);
            return new Counter();
        })!;

        if (Interlocked.Increment(ref counter.Value) > limit)
            throw ApiException.TooManyRequests(ErrorCodes.TooManyAttempts,
                "로그인 시도가 많아 잠시 막혔습니다. 1분 뒤에 다시 해 보세요.");
    }

    private sealed class Counter { public int Value; }

    /// <summary>계정이 없을 때 태울 해시 한 번. 값은 쓰이지 않고 시간만 쓴다.</summary>
    private static class DummyHash
    {
        public static readonly string Value = Hasher.HashPassword(new Member(), "not-a-real-password");
    }
}

/// <summary>비활성 전환 결과 — 화면이 목록을 다시 받지 않고도 줄 하나를 고칠 수 있게.</summary>
public sealed record MemberSnapshotResult(string Username, bool Disabled);
