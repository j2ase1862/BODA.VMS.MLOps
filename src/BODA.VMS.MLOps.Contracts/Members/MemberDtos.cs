namespace BODA.VMS.MLOps.Contracts.Members;

/// <summary>운영 웹 계정에 붙인 MLOps 역할 한 줄.</summary>
public sealed record MemberDto(
    Guid Id,
    string Username,
    string Role,
    string? DisplayName,
    string? Note,
    string GrantedBy,
    DateTime GrantedAt,
    DateTime? LastSeenAt);

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
