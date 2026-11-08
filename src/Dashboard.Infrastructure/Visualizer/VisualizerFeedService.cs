using System.Net.Http.Headers;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Dashboard.Infrastructure.Visualizer;

/// <summary>
/// Keeps one stream open to the vLLM visualizer plugin per source, but only while at least
/// one browser is watching. The plugin arms itself while a stream is connected and goes back
/// to sleep a few seconds after the last one leaves, so "no viewers" means zero extra work
/// inside the model process. Frames are passed through untouched (already JSON).
/// </summary>
public sealed class VisualizerFeedService : IAsyncDisposable
{
    private sealed class Feed
    {
        public readonly HashSet<string> Viewers = new();
        public CancellationTokenSource Cts = new();
        public Task Loop = Task.CompletedTask;
        public VisualizerStatus Status = new("idle");
        public string? LastSnapshot;
    }

    private readonly IVisualizerEndpointResolver _resolver;
    private readonly IVisualizerSink _sink;
    private readonly IHttpClientFactory _http;
    private readonly VisualizerOptions _options;
    private readonly ILogger<VisualizerFeedService> _logger;
    private readonly Dictionary<string, Feed> _feeds = new();
    private readonly object _lock = new();
    private bool _disposed;

    public VisualizerFeedService(
        IVisualizerEndpointResolver resolver, IVisualizerSink sink, IHttpClientFactory http,
        IOptions<VisualizerOptions> options, ILogger<VisualizerFeedService> logger)
    {
        _resolver = resolver;
        _sink = sink;
        _http = http;
        _options = options.Value;
        _logger = logger;
    }

    /// <summary>Number of sources with an open (or opening) stream.</summary>
    public int ActiveFeedCount
    {
        get { lock (_lock) return _feeds.Count; }
    }

    public int ViewerCount(string sourceId)
    {
        lock (_lock) return _feeds.TryGetValue(sourceId, out var f) ? f.Viewers.Count : 0;
    }

    /// <summary>Latest status and frame for a source, so a late joiner is caught up at once.</summary>
    public (VisualizerStatus Status, string? LastSnapshot) Peek(string sourceId)
    {
        lock (_lock)
            return _feeds.TryGetValue(sourceId, out var f) ? (f.Status, f.LastSnapshot) : (new VisualizerStatus("idle"), null);
    }

    public void Join(string connectionId, string sourceId)
    {
        lock (_lock)
        {
            if (_disposed) return;
            if (!_feeds.TryGetValue(sourceId, out var feed))
            {
                feed = new Feed();
                _feeds[sourceId] = feed;
                var f = feed;
                f.Loop = Task.Run(() => RunAsync(sourceId, f, f.Cts.Token));
            }
            feed.Viewers.Add(connectionId);
        }
    }

    public void Leave(string connectionId, string sourceId)
    {
        lock (_lock)
        {
            if (!_feeds.TryGetValue(sourceId, out var feed)) return;
            feed.Viewers.Remove(connectionId);
            StopIfEmpty(sourceId, feed);
        }
    }

    public void Disconnect(string connectionId)
    {
        lock (_lock)
        {
            foreach (var (sourceId, feed) in _feeds.ToList())
            {
                if (feed.Viewers.Remove(connectionId))
                    StopIfEmpty(sourceId, feed);
            }
        }
    }

    private void StopIfEmpty(string sourceId, Feed feed)
    {
        if (feed.Viewers.Count > 0) return;
        feed.Cts.Cancel();
        _feeds.Remove(sourceId);
    }

    private async Task SetStatusAsync(string sourceId, Feed feed, VisualizerStatus status, CancellationToken ct)
    {
        lock (_lock)
        {
            if (feed.Status == status) return;
            feed.Status = status;
        }
        try { await _sink.SendStatusAsync(sourceId, status, ct); }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { _logger.LogDebug(ex, "Visualizer status push failed for {SourceId}.", sourceId); }
    }

    private async Task RunAsync(string sourceId, Feed feed, CancellationToken ct)
    {
        var backoff = TimeSpan.FromSeconds(1);
        var maxBackoff = TimeSpan.FromSeconds(Math.Max(1, _options.MaxReconnectSeconds));
        var minGap = TimeSpan.FromMilliseconds(Math.Max(0, _options.MinFrameIntervalMs));

        while (!ct.IsCancellationRequested)
        {
            try
            {
                var endpoint = await _resolver.ResolveAsync(sourceId, ct);
                if (endpoint is null)
                {
                    await SetStatusAsync(sourceId, feed, new VisualizerStatus("unavailable", "no host configured for this instance"), ct);
                }
                else
                {
                    await SetStatusAsync(sourceId, feed, new VisualizerStatus("connecting"), ct);
                    using var client = _http.CreateClient("visualizer");
                    using var req = new HttpRequestMessage(HttpMethod.Get, endpoint.Url);
                    req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
                    if (!string.IsNullOrEmpty(endpoint.Token))
                        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", endpoint.Token);

                    using var resp = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
                    if (!resp.IsSuccessStatusCode)
                    {
                        await SetStatusAsync(sourceId, feed, new VisualizerStatus("unavailable", $"plugin answered HTTP {(int)resp.StatusCode}"), ct);
                    }
                    else
                    {
                        await SetStatusAsync(sourceId, feed, new VisualizerStatus("live"), ct);
                        backoff = TimeSpan.FromSeconds(1);
                        using var stream = await resp.Content.ReadAsStreamAsync(ct);
                        using var reader = new StreamReader(stream);
                        var lastSent = DateTime.MinValue;
                        string? line;
                        while ((line = await reader.ReadLineAsync(ct)) is not null)
                        {
                            if (!line.StartsWith("data: ", StringComparison.Ordinal)) continue;
                            var now = DateTime.UtcNow;
                            if (now - lastSent < minGap) continue;
                            lastSent = now;
                            var json = line[6..];
                            lock (_lock) feed.LastSnapshot = json;
                            await _sink.SendSnapshotAsync(sourceId, json, ct);
                        }
                        await SetStatusAsync(sourceId, feed, new VisualizerStatus("unavailable", "stream closed by the plugin"), ct);
                    }
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Visualizer stream for {SourceId} failed; will retry.", sourceId);
                try { await SetStatusAsync(sourceId, feed, new VisualizerStatus("unavailable", Short(ex)), ct); }
                catch (OperationCanceledException) { break; }
            }

            try { await Task.Delay(backoff, ct); }
            catch (OperationCanceledException) { break; }
            backoff = TimeSpan.FromTicks(Math.Min(maxBackoff.Ticks, backoff.Ticks * 2));
        }
    }

    private static string Short(Exception ex)
    {
        var m = ex.Message;
        return m.Length > 160 ? m[..160] : m;
    }

    public async ValueTask DisposeAsync()
    {
        List<Feed> feeds;
        lock (_lock)
        {
            _disposed = true;
            feeds = _feeds.Values.ToList();
            _feeds.Clear();
        }
        foreach (var f in feeds) f.Cts.Cancel();
        try { await Task.WhenAll(feeds.Select(f => f.Loop)).WaitAsync(TimeSpan.FromSeconds(2)); }
        catch { /* shutting down */ }
    }
}
