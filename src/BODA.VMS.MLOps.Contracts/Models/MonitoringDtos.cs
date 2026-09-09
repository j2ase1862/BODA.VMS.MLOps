using BODA.VMS.MLOps.Core.Domain;

namespace BODA.VMS.MLOps.Contracts.Models;

/// <summary>
/// 라인에 나간 모델 하나의 상태 (Phase 5 — 모니터링).
/// </summary>
/// <param name="ModelVersionId">우리 레지스트리의 버전</param>
/// <param name="ModelId">그 버전이 속한 모델 계열 — 화면이 상세로 이어 준다</param>
/// <param name="ModelName">모델 계열 이름</param>
/// <param name="Number">버전 번호</param>
/// <param name="Stage">지금 단계</param>
/// <param name="TaskType">작업 유형</param>
/// <param name="Signal">Steady · NotEnoughData · NgRateRose · ConfidenceDropped · InputDrifted</param>
/// <param name="Detail">사람에게 보여 줄 한 줄</param>
/// <param name="BaselineTotal">기준 구간 검사 건수</param>
/// <param name="BaselineNgRate">기준 구간 불량률 0~1</param>
/// <param name="RecentTotal">최근 구간 검사 건수</param>
/// <param name="RecentNgRate">최근 구간 불량률 0~1</param>
/// <param name="PValue">두 불량률 차이가 우연일 확률. 잴 수 없으면 null.</param>
/// <param name="RecentAvgConfidence">최근 구간 DL 신뢰도 평균</param>
/// <param name="RecentConfidenceSamples">그 평균이 몇 건에서 나왔는지</param>
/// <param name="SuggestRetrain">재학습을 권할 만한가. 사람이 정한다 — 자동으로 학습을 걸지 않는다.</param>
/// <param name="SourceJobId">
/// 이 버전을 만든 학습 작업. 있으면 그 설정 그대로 재학습을 열 수 있다.
/// 밖에서 올린 모델은 null 이다 — 그때는 여기서 다시 학습할 수 없고 화면이 그렇게 말한다.
/// </param>
public sealed record ModelHealthDto(
    Guid ModelVersionId,
    Guid ModelId,
    string ModelName,
    int Number,
    ModelStage Stage,
    TaskType TaskType,
    string Signal,
    string Detail,
    int BaselineTotal,
    double BaselineNgRate,
    int RecentTotal,
    double RecentNgRate,
    double? PValue,
    double? RecentAvgConfidence,
    int RecentConfidenceSamples,
    bool SuggestRetrain,
    Guid? SourceJobId);

/// <summary>
/// 모니터링 한 판의 결과.
/// </summary>
/// <param name="Available">
/// 볼 수 있는 상태인가. false 면 <paramref name="Message"/> 가 이유를 말한다 —
/// 주소가 없거나, 운영 웹에 아직 이 API 가 없거나(v1.8.0), 잠깐 닿지 못한 경우다.
/// </param>
/// <param name="Message">사람에게 보여 줄 한 줄</param>
/// <param name="GeneratedAt">언제 본 것인가</param>
/// <param name="Models">모델별 상태</param>
/// <param name="UnmatchedTotal">
/// 우리 모델과 이어지지 않은 검사 건수. 옛 VMS(모델 식별자를 못 싣던 판)이거나
/// DL 을 안 쓴 스텝이다. 이 수가 크면 라인이 아직 옛 판을 쓰고 있다는 뜻이다.
/// </param>
public sealed record ModelMonitorReportDto(
    bool Available,
    string Message,
    DateTime GeneratedAt,
    IReadOnlyList<ModelHealthDto> Models,
    int UnmatchedTotal);
