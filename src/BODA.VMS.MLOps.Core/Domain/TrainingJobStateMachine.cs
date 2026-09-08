namespace BODA.VMS.MLOps.Core.Domain;

/// <summary>
/// 학습 작업 상태 전이 규칙 (Phase 3 §3). 서버가 전이를 검증하고, 워커는 보고만 한다.
/// <code>
/// Queued ─assign→ Assigned ─ack→ Preparing ─start→ Running ─[ONNX]→ Exporting ─upload→ Uploading → Succeeded
/// 활성 상태(Assigned..Uploading) → Failed / Cancelled · Queued → Cancelled
/// Failed(WorkerLost, Attempt&lt;Max) → Queued · Assigned(ack 없음 60s) → Queued
/// </code>
/// </summary>
public static class TrainingJobStateMachine
{
    private static readonly Dictionary<TrainingJobState, TrainingJobState[]> Next = new()
    {
        [TrainingJobState.Queued] = [TrainingJobState.Assigned, TrainingJobState.Cancelled],
        [TrainingJobState.Assigned] = [TrainingJobState.Preparing, TrainingJobState.Queued, TrainingJobState.Failed, TrainingJobState.Cancelled],
        [TrainingJobState.Preparing] = [TrainingJobState.Running, TrainingJobState.Failed, TrainingJobState.Cancelled, TrainingJobState.Queued],
        [TrainingJobState.Running] = [TrainingJobState.Exporting, TrainingJobState.Uploading, TrainingJobState.Failed, TrainingJobState.Cancelled, TrainingJobState.Queued],
        [TrainingJobState.Exporting] = [TrainingJobState.Uploading, TrainingJobState.Failed, TrainingJobState.Cancelled, TrainingJobState.Queued],
        [TrainingJobState.Uploading] = [TrainingJobState.Succeeded, TrainingJobState.Failed, TrainingJobState.Cancelled, TrainingJobState.Queued],
        [TrainingJobState.Succeeded] = [],
        [TrainingJobState.Failed] = [TrainingJobState.Queued],
        [TrainingJobState.Cancelled] = [],
    };

    public static bool CanTransition(TrainingJobState from, TrainingJobState to) =>
        from != to && Next.TryGetValue(from, out var allowed) && allowed.Contains(to);

    public static bool IsTerminal(TrainingJobState s) =>
        s is TrainingJobState.Succeeded or TrainingJobState.Failed or TrainingJobState.Cancelled;

    /// <summary>워커에 배정되어 실행 중으로 간주되는 상태 (하트비트 소실 시 WorkerLost 대상)</summary>
    public static bool IsActiveOnWorker(TrainingJobState s) =>
        s is TrainingJobState.Assigned or TrainingJobState.Preparing or TrainingJobState.Running
          or TrainingJobState.Exporting or TrainingJobState.Uploading;

    /// <summary>워커가 progress 보고로 올릴 수 있는 상태 (Preparing→Running→Exporting→Uploading 순방향만)</summary>
    public static bool IsWorkerReportable(TrainingJobState s) =>
        s is TrainingJobState.Preparing or TrainingJobState.Running or TrainingJobState.Exporting or TrainingJobState.Uploading;
}
