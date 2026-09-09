namespace BODA.VMS.MLOps.Contracts.Lines;

// 라인 PC 서비스 계정 (개발 문서 §5.1 배포 · §5.2 수집).
// 라인 PC 는 사람이 아니라 로그인 화면을 거칠 수 없어, 워커와 같은 방식의 토큰을 쓴다.

public sealed record CreateLineClientRequest(
    string Name,
    /// <summary>이 PC 가 속한 라인. 올라오는 NG 사진과 감사 로그에 이 값이 붙는다.</summary>
    string LineId);

public sealed record LineClientDto(
    Guid Id, string Name, string LineId,
    bool Disabled, string? DisabledReason,
    DateTime? LastSeenAt, string CreatedBy, DateTime CreatedAt);

/// <summary>발급·재발급 응답. 토큰 원문은 여기서 한 번만 나가고 서버에는 해시만 남는다.</summary>
public sealed record CreateLineClientResponse(LineClientDto Line, string Token);

public sealed record DisableLineClientRequest(string? Reason = null);
