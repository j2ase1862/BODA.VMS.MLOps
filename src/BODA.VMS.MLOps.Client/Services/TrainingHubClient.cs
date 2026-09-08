using BODA.VMS.MLOps.Contracts.Training;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.SignalR.Client;

namespace BODA.VMS.MLOps.Client.Services;

/// <summary>
/// SignalR /hubs/training 구독 (Phase 3 §9 실시간 로그).
/// 토큰은 쿼리스트링 access_token 으로 넘긴다 — 서버 JwtBearer 가 /hubs 경로에서 이를 읽는다.
/// </summary>
public sealed class TrainingHubClient(NavigationManager nav, TokenStore tokens) : IAsyncDisposable
{
    private HubConnection? _connection;

    public bool IsConnected => _connection?.State == HubConnectionState.Connected;

    public async Task<HubConnection> ConnectAsync()
    {
        if (_connection is not null) return _connection;

        var token = await tokens.GetAsync();
        _connection = new HubConnectionBuilder()
            .WithUrl(nav.ToAbsoluteUri($"/hubs/training?access_token={Uri.EscapeDataString(token ?? "")}"))
            .WithAutomaticReconnect()
            .Build();
        await _connection.StartAsync();
        return _connection;
    }

    /// <summary>한 작업의 진행률·로그·완료를 구독한다. 반환된 IAsyncDisposable 로 해제한다.</summary>
    public async Task<IAsyncDisposable> WatchJobAsync(Guid jobId,
        Func<TrainingJobDto, Task> onProgress,
        Func<IReadOnlyList<JobLogLineDto>, Task> onLog,
        Func<TrainingJobDto, Task> onDone)
    {
        var conn = await ConnectAsync();
        var subs = new List<IDisposable>
        {
            conn.On<TrainingJobDto>("Progress", job => onProgress(job)),
            conn.On<Guid, IReadOnlyList<JobLogLineDto>>("Log", (_, lines) => onLog(lines)),
            conn.On<TrainingJobDto>("Done", job => onDone(job)),
        };
        await conn.InvokeAsync("JoinJob", jobId);
        return new Subscription(conn, jobId, subs);
    }

    /// <summary>목록 화면용 — 어떤 작업이든 상태가 바뀌면 알린다</summary>
    public async Task<IDisposable> WatchAllJobsAsync(Func<TrainingJobDto, Task> onUpdated)
    {
        var conn = await ConnectAsync();
        return conn.On<TrainingJobDto>("JobUpdated", job => onUpdated(job));
    }

    private sealed class Subscription(HubConnection conn, Guid jobId, List<IDisposable> subs) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            foreach (var s in subs) s.Dispose();
            try
            {
                if (conn.State == HubConnectionState.Connected) await conn.InvokeAsync("LeaveJob", jobId);
            }
            catch { /* 이미 끊긴 연결 */ }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_connection is not null) await _connection.DisposeAsync();
    }
}
