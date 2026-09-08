using Microsoft.Extensions.Options;

namespace BODA.VMS.MLOps.Server.Services;

/// <summary>주기적으로 하트비트 소실(WorkerLost 재큐)·ack 타임아웃 재큐를 처리한다 (Phase 3 §3, §4)</summary>
public sealed class JobSupervisor(IServiceScopeFactory scopes, IOptions<MlopsOptions> options, ILogger<JobSupervisor> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromSeconds(Math.Max(1, options.Value.SupervisorIntervalSec));
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopes.CreateScope();
                var jobs = scope.ServiceProvider.GetRequiredService<TrainingJobService>();
                var changed = await jobs.SuperviseAsync(stoppingToken);
                if (changed > 0) logger.LogInformation("감독자: {Count}건 상태 조정", changed);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
            catch (Exception ex)
            {
                logger.LogError(ex, "감독자 주기 실패");
            }
            try { await Task.Delay(interval, stoppingToken); } catch (OperationCanceledException) { }
        }
    }
}
