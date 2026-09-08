using System.Security.Cryptography;
using BODA.VMS.MLOps.Contracts;
using BODA.VMS.MLOps.Contracts.Workers;
using BODA.VMS.MLOps.Core.Domain;
using BODA.VMS.MLOps.Core.Hashing;
using BODA.VMS.MLOps.Server.Auth;
using BODA.VMS.MLOps.Server.Data;
using BODA.VMS.MLOps.Server.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace BODA.VMS.MLOps.Server.Services;

/// <summary>워커 레지스트리 — 토큰 발급/회전, 등록(자기진단 → Online/Disabled), 하트비트 (Phase 3 §3, §4, §6)</summary>
public sealed class WorkerRegistryService(
    MlopsDbContext db, AuditService audit, ScriptManifestService scripts, IOptions<MlopsOptions> options, TimeProvider clock)
{
    private DateTime Now => clock.GetUtcNow().UtcDateTime;

    public static string NewToken()
    {
        var bytes = RandomNumberGenerator.GetBytes(32);
        return "wk_" + Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    public async Task<CreateWorkerResponse> CreateAsync(CreateWorkerRequest req, CurrentUser user, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.Name))
            throw ApiException.BadRequest(ErrorCodes.Validation, "워커 이름은 필수입니다.");
        var token = NewToken();
        var worker = new Worker
        {
            Id = Guid.NewGuid(), Name = req.Name.Trim(), TokenHash = Sha256Util.HashString(token),
            Status = WorkerStatus.Offline, TaskTypesJson = Mapping.ToJson(req.TaskTypes ?? Enum.GetValues<TaskType>()),
            CreatedAt = Now,
        };
        db.Workers.Add(worker);
        audit.Record(AuditService.Worker, "Created", user.Name, worker.Id.ToString(), new { worker.Name });
        await db.SaveChangesAsync(ct);
        return new CreateWorkerResponse(worker.Id, worker.Name, token);
    }

    public async Task<CreateWorkerResponse> RotateTokenAsync(Guid id, CurrentUser user, CancellationToken ct)
    {
        var worker = await db.Workers.FirstOrDefaultAsync(w => w.Id == id, ct) ?? throw ApiException.NotFound("워커");
        var token = NewToken();
        worker.TokenHash = Sha256Util.HashString(token);
        audit.Record(AuditService.Worker, "TokenRotated", user.Name, id.ToString());
        await db.SaveChangesAsync(ct);
        return new CreateWorkerResponse(worker.Id, worker.Name, token);
    }

    public async Task<WorkerDto> SetDisabledAsync(Guid id, bool disabled, string? reason, CurrentUser user, CancellationToken ct)
    {
        var worker = await db.Workers.FirstOrDefaultAsync(w => w.Id == id, ct) ?? throw ApiException.NotFound("워커");
        worker.AdminDisabled = disabled;
        if (disabled)
        {
            worker.Status = WorkerStatus.Disabled;
            worker.DisabledReason = $"admin: {reason ?? "비활성화"}";
        }
        else
        {
            worker.DisabledReason = null;
            worker.Status = WorkerStatus.Offline; // 다음 하트비트/등록에서 Online
        }
        audit.Record(AuditService.Worker, disabled ? "Disabled" : "Enabled", user.Name, id.ToString(), new { reason });
        await db.SaveChangesAsync(ct);
        return worker.ToDto();
    }

    public async Task<List<WorkerDto>> ListAsync(CancellationToken ct) =>
        (await db.Workers.AsNoTracking().OrderBy(w => w.Name).ToListAsync(ct)).Select(w => w.ToDto()).ToList();

    public async Task<WorkerDto> GetAsync(Guid id, CancellationToken ct) =>
        (await db.Workers.AsNoTracking().FirstOrDefaultAsync(w => w.Id == id, ct) ?? throw ApiException.NotFound("워커")).ToDto();

    /// <summary>워커 시작 시 등록. 프로토콜 불일치·진단 실패·관리자 비활성이면 Disabled (작업 미배정).</summary>
    public async Task<RegisterWorkerResponse> RegisterAsync(Guid workerId, RegisterWorkerRequest req, int protocolVersion, CancellationToken ct)
    {
        var worker = await db.Workers.FirstOrDefaultAsync(w => w.Id == workerId, ct) ?? throw ApiException.NotFound("워커");
        var o = options.Value;

        worker.MachineName = req.MachineName;
        worker.WorkerVersion = req.WorkerVersion;
        worker.CapabilitiesJson = Mapping.ToJson(req.Capabilities);
        worker.TaskTypesJson = Mapping.ToJson(req.TaskTypes);
        worker.ProtocolVersion = protocolVersion;
        worker.DiskFreeGB = req.Capabilities.DiskFreeGB;
        worker.LastHeartbeatAt = Now;
        if (!string.IsNullOrWhiteSpace(req.Name)) worker.Name = req.Name.Trim();

        string? disabledReason = null;
        if (protocolVersion != MlopsJson.ProtocolVersion)
            disabledReason = $"업데이트 필요: 워커 프로토콜 {protocolVersion} ≠ 서버 {MlopsJson.ProtocolVersion}";
        else if (worker.AdminDisabled)
            disabledReason = worker.DisabledReason ?? "admin: 비활성화";
        else if (!req.Capabilities.DiagnosticsOk)
            disabledReason = "진단 실패: " + string.Join("; ", req.Capabilities.DiagnosticFailures ?? []);

        if (disabledReason is not null)
        {
            worker.Status = WorkerStatus.Disabled;
            worker.DisabledReason = disabledReason;
        }
        else
        {
            worker.DisabledReason = null;
            var hasActive = worker.CurrentJobId is not null && await db.TrainingJobs.AnyAsync(j => j.Id == worker.CurrentJobId && j.WorkerId == worker.Id
                && (j.State == TrainingJobState.Assigned || j.State == TrainingJobState.Preparing || j.State == TrainingJobState.Running
                    || j.State == TrainingJobState.Exporting || j.State == TrainingJobState.Uploading), ct);
            if (!hasActive) worker.CurrentJobId = null;
            worker.Status = hasActive ? WorkerStatus.Busy : WorkerStatus.Online;
        }

        audit.Record(AuditService.Worker, "Registered", $"worker:{worker.Name}", worker.Id.ToString(),
            new { req.MachineName, req.WorkerVersion, worker.Status, disabledReason, gpu = req.Capabilities.GpuName });
        await db.SaveChangesAsync(ct);

        var (manifest, _) = scripts.GetManifest();
        return new RegisterWorkerResponse(worker.Id, o.PollIntervalSec, o.HeartbeatSec, manifest, worker.Status, disabledReason);
    }

    public async Task<HeartbeatResponse> HeartbeatAsync(Guid workerId, HeartbeatRequest req, CancellationToken ct)
    {
        var worker = await db.Workers.FirstOrDefaultAsync(w => w.Id == workerId, ct) ?? throw ApiException.NotFound("워커");
        worker.LastHeartbeatAt = Now;
        worker.DiskFreeGB = req.DiskFreeGB;
        worker.GpuMemFreeMB = req.GpuMemFreeMB;

        bool disable = worker.AdminDisabled || worker.Status == WorkerStatus.Disabled;
        if (!disable)
            worker.Status = req.CurrentJobId is not null ? WorkerStatus.Busy : WorkerStatus.Online;

        Guid? cancel = null;
        if (req.CurrentJobId is not null)
        {
            var job = await db.TrainingJobs.AsNoTracking().FirstOrDefaultAsync(j => j.Id == req.CurrentJobId, ct);
            // 서버가 재큐/취소했거나 다른 워커로 넘어간 작업이면 워커에 kill 을 지시한다
            if (job is null || job.WorkerId != worker.Id || !TrainingJobStateMachine.IsActiveOnWorker(job.State) || job.CancelRequested)
                cancel = req.CurrentJobId;
        }
        else
        {
            cancel = await db.TrainingJobs.AsNoTracking()
                .Where(j => j.WorkerId == worker.Id && j.CancelRequested && j.State != TrainingJobState.Cancelled
                            && j.State != TrainingJobState.Succeeded && j.State != TrainingJobState.Failed)
                .Select(j => (Guid?)j.Id).FirstOrDefaultAsync(ct);
        }

        await db.SaveChangesAsync(ct);
        return new HeartbeatResponse(cancel, disable, disable ? worker.DisabledReason : null);
    }
}
