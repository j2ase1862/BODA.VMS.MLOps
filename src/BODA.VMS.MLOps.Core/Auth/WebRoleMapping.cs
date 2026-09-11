namespace BODA.VMS.MLOps.Core.Auth;

/// <summary>
/// 운영 웹(BODA.VMS.Web) 역할 → MLOps 역할.
///
/// <para><b>왜 옮기는가.</b>
/// MLOps 는 자체 계정이 없고 운영 웹이 발급한 JWT 로 들어옵니다. 그런데 운영 웹의 역할은
/// Admin · User · Guest 셋이고 MLOps 는 Viewer ⊂ Labeler ⊂ Engineer ⊂ Admin 을 봅니다.
/// 겹치는 것이 Admin 하나뿐이라, 옮기지 않으면 운영 웹 User·Guest 는 로그인이 403 으로 거부되고
/// MLOps 를 쓰려면 운영 웹 전체 관리자가 돼야 했습니다.
/// </para>
/// <para><b>왜 이 짝인가 (사용자 결정, 2026-09-11).</b>
/// 운영 웹 User 는 라인 작업자가 아니라 엔지니어급입니다 — VMS 에 SSO 로 들어가면 Engineer 로
/// 매핑되어 운영 중인 레시피를 고칩니다(VMS <c>UserService.MapWebRoleToGrade</c>). 이미 라인에서 도는
/// 것을 바꿀 수 있는 신뢰 수준이므로 MLOps Engineer 와 맞습니다. Engineer 는 Production 승격은 못 하지만
/// (Admin 전용) Staging 판을 레시피에 고정 바인딩할 수는 있습니다. Guest 는 조회만 하는 계정입니다.
/// 라인 작업자는 운영 웹 계정이 없습니다(키오스크 사번+PIN, JWT 없음).
/// </para>
/// <para><b>운영 웹 쪽 역할을 늘리지 않은 이유.</b>
/// 운영 웹은 GS 인증 범위 안이고 MLOps 는 밖입니다. 웹에 역할을 더하면 제출 문서·검증 시험이 함께
/// 바뀌므로, 인증 전에는 이쪽에서 옮겨 읽습니다. "라벨링만 하는 사람" 이 필요해지면 MLOps 쪽에
/// 사람별 역할을 두는 것으로 풉니다 (웹 무변경).
/// </para>
/// <para>
/// 서버(JWT 검증 직후)와 관리 화면(토큰을 브라우저에서 읽어 버튼을 보일지 정함)이 <b>같은 규칙</b>을
/// 써야 합니다. 한쪽만 옮기면 권한은 있는데 버튼이 안 보이거나, 버튼은 보이는데 누르면 403 입니다.
/// </para>
/// </summary>
public static class WebRoleMapping
{
    /// <summary>운영 웹 역할 → 더해 줄 MLOps 역할. Admin 은 이름이 같아 옮길 것이 없다.</summary>
    public static readonly IReadOnlyDictionary<string, string> Map =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["User"] = "Engineer",
            ["Guest"] = "Viewer",
        };

    /// <summary>
    /// 받은 역할에 옮긴 역할을 더해 돌려준다. 원래 역할은 지우지 않는다 (감사 로그·화면에서 어디서 왔는지 보이게).
    /// 여러 번 불러도 결과가 같다 — 인증 처리는 한 요청에서도 여러 번 돌 수 있다.
    /// </summary>
    public static IReadOnlyList<string> Expand(IEnumerable<string> roles)
    {
        var result = new List<string>();
        foreach (var role in roles)
        {
            if (!result.Contains(role, StringComparer.Ordinal)) result.Add(role);
            if (Map.TryGetValue(role, out var mapped) && !result.Contains(mapped, StringComparer.Ordinal))
                result.Add(mapped);
        }
        return result;
    }
}
