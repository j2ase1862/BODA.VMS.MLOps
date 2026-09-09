namespace BODA.VMS.MLOps.Server.Data.Entities;

/// <summary>
/// 라인 PC 서비스 계정 (개발 문서 §5.1 배포 · §5.2 수집).
///
/// <para>
/// 라인 PC 는 사람이 아니라서 로그인 화면을 거칠 수 없다. 그렇다고 워커 토큰을 쓰게 하면
/// 학습 작업 큐와 아티팩트 업로드까지 열리므로, 워커와 같은 방식이되 범위가 다른 토큰을 따로 둔다.
/// 이 토큰이 하는 일은 둘뿐이다 — 모델 참조를 풀어 아티팩트를 받고, NG 사진을 올리는 것.
/// </para>
/// <para>
/// 토큰 원문은 발급 응답에 한 번만 나가고 서버에는 SHA-256 해시만 남는다 (워커 토큰과 같다).
/// 잃어버리면 다시 발급받아야 하고, 그 순간 이전 토큰은 못 쓰게 된다.
/// </para>
/// </summary>
public class LineClient
{
    public Guid Id { get; set; }

    /// <summary>사람이 알아보는 이름. 보통 라인 이름을 그대로 쓴다.</summary>
    public string Name { get; set; } = "";

    /// <summary>
    /// 이 PC 가 속한 라인. 올라오는 NG 사진에 이 값이 붙고, 모델 참조 감사 로그에도 남는다.
    /// </summary>
    public string LineId { get; set; } = "";

    public string TokenHash { get; set; } = "";

    /// <summary>관리자가 끈 계정. 인증 단계에서 막는다 — 끊었다는 조치가 실제로 성립해야 한다.</summary>
    public bool Disabled { get; set; }
    public string? DisabledReason { get; set; }

    /// <summary>마지막으로 이 토큰이 쓰인 시각. 죽은 라인을 찾는 데 쓴다.</summary>
    public DateTime? LastSeenAt { get; set; }

    public string CreatedBy { get; set; } = "";
    public DateTime CreatedAt { get; set; }
}
