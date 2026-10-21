using VMS.Core.Services;

namespace BODA.VMS.MLOps.TrainWorker.Jobs;

/// <summary>
/// 같은 PC 의 VMS 가 지금 검사(AutoRun · Roller 연속 검사) 중인지 (개발 문서 §5.6).
/// 시험에서 바꿔 끼우려고 감쌌다 — 실제 판단은 VMS 가 정한 <see cref="InspectionActivitySignal"/> 하나다.
/// </summary>
public interface IInspectionSignal
{
    bool IsActive();
}

/// <summary>
/// VMS 가 검사 동안 쥐는 명명 Mutex(<see cref="InspectionActivitySignal.MutexName"/>)가 있는지 본다.
/// 워커는 이 이름을 직접 열지 않는다 — 열 권한이 없을 때를 "검사 중" 으로 치는 규칙까지 VMS 쪽 헬퍼에 있다.
/// 전용 워커 PC 에는 VMS 가 없으므로 언제나 거짓이다.
/// </summary>
public sealed class VmsInspectionSignal : IInspectionSignal
{
    public bool IsActive() => InspectionActivitySignal.IsActive();
}
