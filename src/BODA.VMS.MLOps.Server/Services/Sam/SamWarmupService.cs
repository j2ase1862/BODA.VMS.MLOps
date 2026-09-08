namespace BODA.VMS.MLOps.Server.Services.Sam;

/// <summary>
/// 서버가 뜰 때 SAM 모델을 뒤에서 미리 올린다 (개발 문서 §5.4).
///
/// <para>
/// ONNX 세션을 여는 데만 1초 남짓 걸린다. 그 값을 첫 라벨러가 치르면 "SAM 이 느리다" 는 인상만 남는다.
/// 시작을 막지 않도록 별도 작업으로 돌리고, 실패해도 서버는 그대로 뜬다 —
/// 모델이 없거나 깨졌으면 <c>/api/sam/status</c> 가 이유를 말해 준다.
/// </para>
/// </summary>
public sealed class SamWarmupService(SamAssistService sam, ILogger<SamWarmupService> logger) : IHostedService
{
    private Task? _warming;
    private readonly CancellationTokenSource _stopping = new();

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _warming = Task.Run(async () =>
        {
            await sam.WarmAsync(_stopping.Token);
            var (available, ready, message) = sam.Status();
            if (available && ready)
                logger.LogInformation("SAM 보조 준비 완료 · 후보 {Mode}", sam.MultiMask ? "여러 개" : "하나");
            else if (message is not null)
                logger.LogInformation("SAM 보조 사용 안 함 — {Message}", message);
        }, CancellationToken.None);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await _stopping.CancelAsync();
        if (_warming is not null)
        {
            try { await _warming.WaitAsync(cancellationToken); } catch (Exception) { /* 종료 중이라 무시 */ }
        }
        _stopping.Dispose();
    }
}
