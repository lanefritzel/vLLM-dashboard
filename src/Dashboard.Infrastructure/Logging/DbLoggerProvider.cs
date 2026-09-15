using System.Threading.Channels;
using Microsoft.Extensions.Logging;

namespace Dashboard.Infrastructure.Logging;

/// <summary>One captured log line, queued for the background writer — see
/// DbLogWriterService. Deliberately a plain record with no DB dependency, so the
/// logger itself (on the hot path of every request) never touches EF Core.</summary>
public sealed record AppLogRecord(DateTime TimestampUtc, string Level, string Category, string Message, string? Exception);

/// <summary>Writes log lines into an in-memory channel for a background service to
/// batch into the database — see DbLogWriterService. `docker logs` is tied to the
/// container's lifetime and gets wiped on every redeploy; a real incident needed
/// forensic evidence to survive that. Categories under "Dashboard." (this app's own
/// code) are captured from Information up; everything else (ASP.NET Core/EF Core
/// framework noise) only from Warning up, to avoid flooding the table with routine
/// per-request/per-SQL-command chatter that `docker logs` already shows live.</summary>
public sealed class DbLoggerProvider : ILoggerProvider
{
    private readonly ChannelWriter<AppLogRecord> _writer;
    private readonly LogLevel _minLevelForApp;
    private readonly LogLevel _minLevelForOther;

    public DbLoggerProvider(ChannelWriter<AppLogRecord> writer, LogLevel minLevelForApp, LogLevel minLevelForOther)
    {
        _writer = writer;
        _minLevelForApp = minLevelForApp;
        _minLevelForOther = minLevelForOther;
    }

    public ILogger CreateLogger(string categoryName)
    {
        var minLevel = categoryName.StartsWith("Dashboard.", StringComparison.Ordinal) ? _minLevelForApp : _minLevelForOther;
        return new DbLogger(categoryName, _writer, minLevel);
    }

    public void Dispose()
    {
    }
}

internal sealed class DbLogger : ILogger
{
    private readonly string _category;
    private readonly ChannelWriter<AppLogRecord> _writer;
    private readonly LogLevel _minLevel;

    public DbLogger(string category, ChannelWriter<AppLogRecord> writer, LogLevel minLevel)
    {
        _category = category;
        _writer = writer;
        _minLevel = minLevel;
    }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None && logLevel >= _minLevel;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(logLevel)) return;

        // TryWrite on an unbounded channel always completes immediately — never blocks
        // the caller, never waits on the database. Logging must never be able to slow
        // down or fail the actual request it's describing.
        _writer.TryWrite(new AppLogRecord(
            DateTime.UtcNow, logLevel.ToString(), _category, formatter(state, exception), exception?.ToString()));
    }
}
