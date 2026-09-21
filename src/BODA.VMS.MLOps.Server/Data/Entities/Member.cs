namespace BODA.VMS.MLOps.Server.Data.Entities;

/// <summary>
/// 운영 웹 계정에 붙인 MLOps 역할 (개발 문서 §6).
///
/// <para>
/// <b>인증은 운영 웹이, 인가는 우리가 한다.</b> 로그인은 BODA.VMS.Web 이 확인하고 토큰을 내주지만,
/// 그 토큰의 역할은 운영 웹의 것(<c>Admin</c>·<c>User</c>)이라 우리 역할 사다리
/// (Viewer ⊂ Labeler ⊂ Engineer ⊂ Admin)와 맞지 않는다. 그대로 쓰면 운영 웹의 일반 사용자는
/// MLOps 의 어느 정책도 통과하지 못해 모든 화면에서 403 을 받는다 — 실제로 그랬다.
/// </para>
/// <para>
/// 그래서 "이 계정은 MLOps 에서 무엇을 할 수 있나" 는 이 표가 정한다. 운영 웹에는 손대지 않는다 —
/// 그쪽은 GS 인증 범위 안이라 고치면 인증에 영향이 간다.
/// </para>
/// </summary>
public class Member
{
    public Guid Id { get; set; }

    /// <summary>
    /// 운영 웹의 계정 이름. 토큰의 <c>unique_name</c> 과 대조하므로 <b>소문자로 정규화해</b> 저장한다
    /// (<see cref="Normalize"/>). 대소문자가 다른 두 줄이 생기면 어느 쪽이 이겼는지 아무도 모른다.
    /// </summary>
    public string Username { get; set; } = "";

    /// <summary>
    /// MLOps 역할 하나 (<see cref="Auth.Roles.Viewer"/>·<see cref="Auth.Roles.Labeler"/>·
    /// <see cref="Auth.Roles.Engineer"/>·<see cref="Auth.Roles.Admin"/>).
    /// 역할이 사다리라 여러 개를 둘 이유가 없다 — 위의 것이 아래를 모두 포함한다.
    /// </summary>
    public string Role { get; set; } = "";

    /// <summary>마지막으로 본 표시 이름. 목록에서 사람을 알아보는 용도이고 판단에는 쓰지 않는다.</summary>
    public string? DisplayName { get; set; }

    /// <summary>왜 이 역할을 주었는지. 비워도 된다.</summary>
    public string? Note { get; set; }

    public string GrantedBy { get; set; } = "";
    public DateTime GrantedAt { get; set; }

    /// <summary>이 계정이 마지막으로 MLOps 에 들어온 시각. 로그인할 때만 갱신한다.</summary>
    public DateTime? LastSeenAt { get; set; }

    // ── 아래는 자체 계정 로그인(Auth:Mode=Local)에서만 쓴다. Web 모드에서는 전부 비어 있다. ──

    /// <summary>
    /// PBKDF2 해시 (<c>PasswordHasher&lt;Member&gt;</c> 형식). 비어 있으면 아직 비밀번호가 없는 줄이다 —
    /// Web 모드에서 만든 줄이거나, Local 모드에서 관리자가 아직 임시 비밀번호를 내주지 않은 계정이다.
    /// </summary>
    public string? PasswordHash { get; set; }

    /// <summary>마지막으로 비밀번호가 바뀐 시각. 화면에 보여 주는 용도이고 판단에는 쓰지 않는다.</summary>
    public DateTime? PasswordUpdatedAt { get; set; }

    /// <summary>
    /// 토큰 세대. 비밀번호를 바꾸거나 초기화할 때마다 새로 찍고, 발급한 토큰에 실어 매 요청 대조한다 —
    /// 그래서 비밀번호를 바꾸면 <b>다른 자리에 남아 있던 세션이 그 즉시 끊긴다</b>.
    /// 운영 웹이 토큰 세대(<c>tv</c>)로 하는 일과 같고, 로컬 모드에는 물어볼 그쪽이 없어 여기서 한다.
    ///
    /// <para>
    /// <b>시각이 아니라 난수다.</b> 시각으로 두면 같은 틱 안에 두 번 바뀔 때 옛 토큰이 살아남는다 —
    /// 가짜 시계로 도는 시험에서는 매번 그렇게 된다.
    /// </para>
    /// </summary>
    public string? SecurityStamp { get; set; }

    /// <summary>임시 비밀번호 상태. 바꾸기 전에는 역할을 주지 않아 다른 화면에 들어가지 못한다.</summary>
    public bool MustChangePassword { get; set; }

    /// <summary>
    /// 비활성. 인증이 아니라 <see cref="Auth.MemberRoleClaimsTransformation"/> 에서 막는다 —
    /// 그 자리가 요청마다 돌고 캐시를 즉시 버릴 수 있어, 살아 있는 토큰도 바로 듣지 않게 된다.
    /// <b>두 모드 모두에 적용된다</b> (운영 웹 계정도 여기서 끊을 수 있다).
    /// </summary>
    public bool Disabled { get; set; }

    /// <summary>연속 로그인 실패 횟수. 성공하면 0 으로 돌아간다.</summary>
    public int FailedCount { get; set; }

    /// <summary>이 시각까지 잠겨 있다. null 이면 잠겨 있지 않다.</summary>
    public DateTime? LockedUntil { get; set; }

    /// <summary>계정 이름 정규화 — 저장과 조회가 같은 규칙을 써야 한다.</summary>
    public static string Normalize(string username) => username.Trim().ToLowerInvariant();
}
