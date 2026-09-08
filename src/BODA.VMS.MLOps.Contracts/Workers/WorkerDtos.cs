using BODA.VMS.MLOps.Core.Domain;

namespace BODA.VMS.MLOps.Contracts.Workers;

/// <summary>워커 자기진단 결과 (Phase 3 §3 Capabilities, §6)</summary>
public sealed record WorkerCapabilities(
    string? GpuName, int GpuMemMB, string? CudaVersion, bool CudaAvailable,
    string? PythonVersion, Dictionary<string, string> Packages,
    string? ScriptsHash, double DiskFreeGB, string WorkerVersion,
    string[]? OnnxRuntimeProviders = null,
    string[]? DiagnosticFailures = null)
{
    public bool DiagnosticsOk => DiagnosticFailures is null || DiagnosticFailures.Length == 0;
}

public sealed record RegisterWorkerRequest(string Name, string MachineName, WorkerCapabilities Capabilities, TaskType[] TaskTypes, string WorkerVersion);

public sealed record RegisterWorkerResponse(Guid WorkerId, int PollIntervalSec, int HeartbeatSec, Dictionary<string, string> ScriptsManifest, WorkerStatus Status, string? DisabledReason);

public sealed record HeartbeatRequest(WorkerStatus Status, Guid? CurrentJobId, double DiskFreeGB, int GpuMemFreeMB);

public sealed record HeartbeatResponse(Guid? CancelRequested, bool Disable, string? DisabledReason);

/// <summary>Admin 이 워커를 등록하고 토큰을 1회 발급 (Phase 3 §4 인증)</summary>
public sealed record CreateWorkerRequest(string Name, TaskType[]? TaskTypes = null);

public sealed record CreateWorkerResponse(Guid WorkerId, string Name, string Token);

public sealed record WorkerDto(
    Guid Id, string Name, string? MachineName, WorkerStatus Status, string? DisabledReason, DateTime? LastHeartbeatAt,
    WorkerCapabilities? Capabilities, TaskType[] TaskTypes, int MaxConcurrent, Guid? CurrentJobId, DateTime CreatedAt, string? WorkerVersion);

/// <summary>서버가 배포하는 스크립트 해시 목록 — 워커는 일치하는 것만 실행 (Phase 3 §2, §8)</summary>
public sealed record ScriptsManifest(Dictionary<string, string> Scripts, DateTime GeneratedAt);
