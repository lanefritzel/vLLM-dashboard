using Dashboard.Core.Abstractions;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Dashboard.Application.Logs;

public interface ILogStreamManager
{
    Task JoinAsync(string connectionId, string sourceId);
    Task LeaveAsync(string connectionId, string sourceId);
    Task DisconnectAsync(string connectionId);
}

/// <summary>
/// Ref-counts viewers per source and runs exactly one Docker log-follow connection
/// per source while at least one viewer is watching — starting on the first join,
/// stopping on the last leave (or disconnect). Reconnects automatically if the
/// underlying stream ends while viewers remain (e.g. the container was hot-swapped
/// out from under a live log view), rather than leaving the panel silently frozen.
/// </summary>
public sealed class LogStreamManager : ILogStreamManager
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IHubContext<LogsHub> _hub;
    private readonly ILogger<LogStreamManager> _logger;
    private readonly object _lock = new();

    private sealed class StreamState
    {
        public int RefCount;
        public CancellationTokenSource Cts = new();
    }

    private readonly Dictionary<string, StreamState> _streams = new();
    private readonly Dictionary<string, HashSet<string>> _joinedByConnection = new();

    public LogStreamManager(IServiceScopeFactory scopeFactory, IHubContext<LogsHub> hub, ILogger<LogStreamManager> logger)
    {
        _scopeFactory = scopeFactory;
        _hub = hub;
        _logger = logger;
    }

    public Task JoinAsync(string connectionId, string sourceId)
    {
        StreamState? toStart = null;
        lock (_lock)
        {
            var joined = _joinedByConnection.TryGetValue(connectionId, out var set) ? set : _joinedByConnection[connectionId] = new();
            if (!joined.Add(sourceId))
                return Task.CompletedTask; // already joined from this connection — idempotent

            if (!_streams.TryGetValue(sourceId, out var state))
            {
                state = new StreamState();
                _streams[sourceId] = state;
                toStart = state;
            }
            state.RefCount++;
        }

        if (toStart is not null)
            _ = Task.Run(() => RunStreamAsync(sourceId, toStart));
        return Task.CompletedTask;
    }

    public Task LeaveAsync(string connectionId, string sourceId)
    {
        RemoveJoin(connectionId, sourceId);
        return Task.CompletedTask;
    }

    public Task DisconnectAsync(string connectionId)
    {
        string[] sourceIds;
        lock (_lock)
        {
            if (!_joinedByConnection.TryGetValue(connectionId, out var set))
                return Task.CompletedTask;
            sourceIds = set.ToArray();
        }
        foreach (var sourceId in sourceIds)
            RemoveJoin(connectionId, sourceId);
        return Task.CompletedTask;
    }

    private void RemoveJoin(string connectionId, string sourceId)
    {
        CancellationTokenSource? toStop = null;
        lock (_lock)
        {
            if (_joinedByConnection.TryGetValue(connectionId, out var joined))
            {
                if (!joined.Remove(sourceId))
                    return; // wasn't joined from this connection — nothing to do
                if (joined.Count == 0)
                    _joinedByConnection.Remove(connectionId);
            }

            if (!_streams.TryGetValue(sourceId, out var state))
                return;
            state.RefCount--;
            if (state.RefCount <= 0)
            {
                _streams.Remove(sourceId);
                toStop = state.Cts;
            }
        }
        toStop?.Cancel();
    }

    private async Task RunStreamAsync(string sourceId, StreamState state)
    {
        var ct = state.Cts.Token;
        while (!ct.IsCancellationRequested)
        {
            using var scope = _scopeFactory.CreateScope();
            var sources = scope.ServiceProvider.GetRequiredService<ISourceCatalog>();
            var orchestrator = scope.ServiceProvider.GetRequiredService<IDockerOrchestrator>();

            try
            {
                var source = await sources.GetByIdOrSlugAsync(sourceId, ct);
                if (source is null)
                {
                    _logger.LogWarning("Log stream: unknown source {SourceId}.", sourceId);
                    return;
                }

                await orchestrator.StreamLogsAsync(source, line =>
                    _hub.Clients.Group(LogsHub.GroupFor(sourceId)).SendAsync(LogsHub.LineEvent, line, CancellationToken.None), ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Log stream for {SourceId} interrupted; retrying shortly if still watched.", sourceId);
            }

            if (ct.IsCancellationRequested)
                return;
            try
            {
                // The stream ending on its own (container stopped/recreated, e.g. a
                // hot-swap) isn't cancellation — reconnect after a short delay instead
                // of leaving viewers staring at a frozen panel.
                await Task.Delay(TimeSpan.FromSeconds(2), ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }
}
