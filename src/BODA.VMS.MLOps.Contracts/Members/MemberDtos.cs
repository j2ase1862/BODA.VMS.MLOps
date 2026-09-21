namespace BODA.VMS.MLOps.Contracts.Members;

/// <summary>
/// 계정에 붙인 MLOps 역할 한 줄.
/// 뒤의 넷은 자체 계정 로그인(<c>Auth:Mode=Local</c>)에서만 뜻이 있고, Web 모드에서는 비어 있습니다.
/// </summary>
/// <param name="Disabled">비활성. 두 모드 모두에 적용됩니다 — 살아 있는 토큰도 다음 요청부터 막힙니다.</param>
/// <param name="HasPassword">비밀번호가 설정되어 있는지. false 면 임시 비밀번호를 내주기 전까지 로그인할 수 없습니다.</param>
/// <param name="MustChangePassword">임시 비밀번호 상태. 바꾸기 전에는 다른 화면에 들어가지 못합니다.</param>
/// <param name="LockedUntil">연속 실패로 잠긴 계정이 풀리는 시각.</param>
public sealed record MemberDto(
    Guid Id,
    string Username,
    string Role,
    string? DisplayName,
    string? Note,
    string GrantedBy,
    DateTime GrantedAt,
    DateTime? LastSeenAt,
    bool Disabled = false,
    bool HasPassword = false,
    bool MustChangePassword = false,
    DateTime? LockedUntil = null);

/// <summary>계정을 쓰지 못하게 하거나 되살리기.</summary>
public sealed record SetMemberDisabledRequest(bool Disabled);

/// <summary>
/// 역할 주기. 같은 계정이 이미 있으면 역할을 바꾼다 — 한 계정에 줄은 하나다.
/// </summary>
public sealed record GrantMemberRequest(string Username, string Role, string? DisplayName = null, string? Note = null);

/// <summary>
/// 지금 서버가 어떻게 판단하는지. 화면이 "표에 없는 사람은 무엇이 되나" 를 설명하는 데 쓴다.
/// </summary>
/// <param name="DefaultRole">표에 없는 계정이 받는 역할. 빈 값이면 아무 화면도 열리지 않는다.</param>
/// <param name="BootstrapWebRole">이 역할을 달고 온 토큰은 표에 없어도 Admin 으로 인정한다. 빈 값이면 그 통로가 닫혀 있다.</param>
/// <param name="AssignableRoles">줄 수 있는 역할 목록 (낮은 것부터).</param>
public sealed record MemberPolicyDto(string DefaultRole, string BootstrapWebRole, string[] AssignableRoles);
