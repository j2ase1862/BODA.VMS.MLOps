namespace BODA.VMS.MLOps.Contracts.Models;

/// <summary>
/// 라인 PC 가 모델을 받아 간 기록 한 줄.
///
/// <para>감사 로그(Delivery 범주)를 화면에서 읽기 좋게 편 것이다. 사고가 났을 때 "그 라인이 그때 어느
/// 버전을 쓰고 있었나" 에 답하기 위한 것으로, 예전에는 라인의 마지막 접속 시각으로만 간접 추정할 수
/// 있었다.</para>
/// </summary>
/// <param name="At">받아 간 시각 (UTC)</param>
/// <param name="Action">Resolved = 어느 버전인지 물어본 것, Downloaded = 파일을 실제로 받아 간 것</param>
/// <param name="LineId">라인 식별자. 라인 토큰이 아닌 주체(엔지니어가 직접 받은 경우 등)면 비어 있다</param>
/// <param name="Actor">요청 주체 이름 (라인 토큰이면 라인 계정 이름)</param>
/// <param name="ModelVersionId">받아 간 모델 버전</param>
/// <param name="Number">그 버전의 번호 (v3 의 3)</param>
/// <param name="Stage">물어본 시점의 스테이지 (Resolved 에만 있다)</param>
/// <param name="Asked">라인이 무엇을 달라고 했는지 — "production" · "staging" · "v3" (Resolved 에만 있다)</param>
public sealed record ModelDeliveryDto(
    DateTime At,
    string Action,
    string? LineId,
    string Actor,
    Guid ModelVersionId,
    int? Number,
    string? Stage,
    string? Asked);
