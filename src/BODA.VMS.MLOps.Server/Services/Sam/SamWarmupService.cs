namespace BODA.VMS.MLOps.Server.Services.Sam;

/// <summary>
/// 서버가 뜰 때 SAM 모델을 뒤에서 미리 올린다 (개발 문서 §5.4).
///
/// <para>
/// ONNX 세션을 여는 데만 1초 남짓 걸린다. 그 값을 첫 라벨러가 치르면 "SAM 이 느리다" 는 인상만 남는다.
/// 시작을 막지 않도록 별도 작업으로 돌리고, 실패해도 서버는 그대로 뜬다 —
/// 모델이 없거나 깨졌으면 <c>/api/sam/status</c> 가 이유를 말해 준다.
/// </para>
/// <para>
/// 종료는 한 번만 오지 않는다. WebApplicationFactory 는 동기·비동기 정리 경로에서 호스트를 두 번 멈추므로,
/// 두 번째 호출이 이미 버린 토큰 원본을 건드리지 않도록 막아 둔다. 그러지 않으면
/// 시험 정리 단계가 ObjectDisposedException 으로 시끄러워진다.
/// </para>
/// </summary>
public sealed class SamWarmupService(SamAssistService sam, ILogger<SamWarmupService> logger)
    : IHostedService, IDisposable
{
    private readonly CancellationTokenSource _stopping = new();
    private Task? _warming;
    private int _stopped;
    private int _disposed;

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
        // 두 번째 종료 호출은 할 일이 없다
        if (Interlocked.Exchange(ref _stopped, 1) == 1) return;

        try { await _stopping.CancelAsync(); }
        catch (ObjectDisposedException) { return; }

        if (_warming is not null)
        {
            try { await _warming.WaitAsync(cancellationToken); }
            catch (Exception) { /* 종료 중이라 무시 */ }
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
        _stopping.Dispose();
    }
}
