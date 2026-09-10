using System.Text;
using Microsoft.Extensions.Logging;

namespace BODA.VMS.MLOps.TrainWorker;

/// <summary>
/// 날짜별 파일 로그 (Phase 3 §10: %ProgramData%\BODA VMS TrainWorker\logs\worker-yyyyMMdd.log).
/// 서비스로 돌 때 콘솔 출력은 아무 데도 가지 않고 이벤트 로그는 현장에서 읽기 불편하다 —
/// 설치 가이드가 "로그 폴더를 열어 보라" 고 말할 수 있어야 한다. 외부 패키지 없이 최소로 둔다.
/// 보존: 시작할 때 <see cref="RetentionDays"/> 보다 오래된 파일을 지운다.
/// </summary>
public sealed class FileLoggerProvider : ILoggerProvider
{
    private readonly string _dir;
    private readonly object _gate = new();
    private StreamWriter? _writer;
    private DateOnly _writerDate;

    public int RetentionDays { get; init; } = 14;
    public LogLevel MinLevel { get; init; } = LogLevel.Information;

    public FileLoggerProvider(string dir)
    {
        _dir = dir;
        try
        {
            Directory.CreateDirectory(dir);
            var cutoff = DateTime.UtcNow.AddDays(-RetentionDays);
            foreach (var f in Directory.GetFiles(dir, "worker-*.log"))
                if (File.GetLastWriteTimeUtc(f) < cutoff) { try { File.Delete(f); } catch { } }
        }
        catch { /* 로그 폴더를 못 만들면 파일 로그만 조용히 빠진다 — 워커는 계속 돈다 */ }
    }

    public ILogger CreateLogger(string categoryName) => new FileLogger(this, categoryName);

    internal void Write(LogLevel level, string category, string message, Exception? ex)
    {
        if (level < MinLevel) return;
        var now = DateTime.Now;
        var line = new StringBuilder(256)
            .Append(now.ToString("yyyy-MM-dd HH:mm:ss.fff")).Append(' ')
            .Append(Abbrev(level)).Append(' ')
            .Append(Short(category)).Append(": ")
            .Append(message);
        if (ex is not null) line.AppendLine().Append(ex);
        lock (_gate)
        {
            try
            {
                var today = DateOnly.FromDateTime(now);
                if (_writer is null || today != _writerDate)
                {
                    _writer?.Dispose();
                    _writer = new StreamWriter(Path.Combine(_dir, $"worker-{today:yyyyMMdd}.log"), append: true, new UTF8Encoding(false)) { AutoFlush = true };
                    _writerDate = today;
                }
                _writer.WriteLine(line.ToString());
            }
            catch { /* 디스크 가득·권한 — 로그 때문에 워커를 멈추지 않는다 */ }
        }
    }

    private static string Abbrev(LogLevel l) => l switch
    {
        LogLevel.Trace => "TRC", LogLevel.Debug => "DBG", LogLevel.Information => "INF",
        LogLevel.Warning => "WRN", LogLevel.Error => "ERR", LogLevel.Critical => "CRT", _ => "???",
    };

    private static string Short(string category)
    {
        var i = category.LastIndexOf('.');
        return i < 0 ? category : category[(i + 1)..];
    }

    public void Dispose()
    {
        lock (_gate) { _writer?.Dispose(); _writer = null; }
    }

    private sealed class FileLogger(FileLoggerProvider owner, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => logLevel >= owner.MinLevel;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            owner.Write(logLevel, category, formatter(state, exception), exception);
        }
    }
}
