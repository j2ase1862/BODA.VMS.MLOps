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

    /// <summary>계정 이름 정규화 — 저장과 조회가 같은 규칙을 써야 한다.</summary>
    public static string Normalize(string username) => username.Trim().ToLowerInvariant();
}
