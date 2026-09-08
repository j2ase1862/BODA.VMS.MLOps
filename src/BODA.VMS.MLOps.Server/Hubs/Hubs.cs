using BODA.VMS.MLOps.Contracts.Models;
using BODA.VMS.MLOps.Contracts.Training;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace BODA.VMS.MLOps.Server.Hubs;

/// <summary>SignalR /hubs/models — VersionCreated · StagePromoted · BindingChanged (Phase 1 §5). 브라우저·VMS 구독.</summary>
[Authorize(Policy = Auth.Policies.Viewer)]
public sealed class ModelsHub : Hub;

/// <summary>SignalR /hubs/training — 그룹 job:{id} 에 Progress · Log · Done (Phase 3 §4)</summary>
[Authorize(Policy = Auth.Policies.Viewer)]
public sealed class TrainingHub : Hub
{
    public static string JobGroup(Guid jobId) => $"job:{jobId:N}";

    public Task JoinJob(Guid jobId) => Groups.AddToGroupAsync(Context.ConnectionId, JobGroup(jobId));
    public Task LeaveJob(Guid jobId) => Groups.RemoveFromGroupAsync(Context.ConnectionId, JobGroup(jobId));
}

/// <summary>서비스 계층이 쓰는 브로드캐스트 인터페이스 — 테스트에서 대체 가능</summary>
public interface IMlopsNotifier
{
    Task VersionCreated(ModelVersionDto version);
    Task StagePromoted(ModelVersionDto version);
    Task BindingChanged(string recipeId, string toolId, Guid? modelVersionId);
    Task JobProgress(TrainingJobDto job);
    Task JobLog(Guid jobId, IReadOnlyList<JobLogLineDto> lines);
    Task JobDone(TrainingJobDto job);
    Task JobQueued(TrainingJobDto job);
}

public sealed class SignalRNotifier(IHubContext<ModelsHub> models, IHubContext<TrainingHub> training) : IMlopsNotifier
{
    public Task VersionCreated(ModelVersionDto version) => models.Clients.All.SendAsync("VersionCreated", version);
    public Task StagePromoted(ModelVersionDto version) => models.Clients.All.SendAsync("StagePromoted", version);
    public Task BindingChanged(string recipeId, string toolId, Guid? modelVersionId) =>
        models.Clients.All.SendAsync("BindingChanged", recipeId, toolId, modelVersionId);

    public Task JobProgress(TrainingJobDto job) => Task.WhenAll(
        training.Clients.Group(TrainingHub.JobGroup(job.Id)).SendAsync("Progress", job),
        training.Clients.All.SendAsync("JobUpdated", job));
    public Task JobLog(Guid jobId, IReadOnlyList<JobLogLineDto> lines) =>
        training.Clients.Group(TrainingHub.JobGroup(jobId)).SendAsync("Log", jobId, lines);
    public Task JobDone(TrainingJobDto job) => Task.WhenAll(
        training.Clients.Group(TrainingHub.JobGroup(job.Id)).SendAsync("Done", job),
        training.Clients.All.SendAsync("JobUpdated", job));
    public Task JobQueued(TrainingJobDto job) => training.Clients.All.SendAsync("JobUpdated", job);
}
