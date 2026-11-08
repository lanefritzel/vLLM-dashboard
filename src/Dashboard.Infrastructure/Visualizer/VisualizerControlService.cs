using System.Net.Http.Headers;
using System.Text.Json;
using Dashboard.Core.Abstractions;
using Microsoft.Extensions.Logging;

namespace Dashboard.Infrastructure.Visualizer;

/// <summary>What the plugin says about itself. <see cref="Reachable"/> false means no answer.</summary>
public sealed record VisualizerPluginState(bool Reachable, bool Enabled, bool PendingDisable, bool PendingEnable, string? Reason)
{
    public static readonly VisualizerPluginState Unreachable = new(false, false, false, false, null);
}

/// <summary>
/// The dashboard's on/off switch for the vLLM visualizer plugin. Both directions are
/// graceful: "off" takes effect once the model has gone quiet after the current answer,
/// "on" at the start of the next request, so the tap never changes state mid-answer
/// (the plugin enforces this; a "now" override skips the wait). The chosen state is
/// remembered per instance and re-applied after vLLM restarts, since the plugin itself
/// comes back "on" each time the model process starts.
/// </summary>
public sealed class VisualizerControlService
{
    private static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(5);

    private readonly IVisualizerEndpointResolver _resolver;
    private readonly IHttpClientFactory _http;
    private readonly IConfigStore _config;
    private readonly ILogger<VisualizerControlService> _logger;

    public VisualizerControlService(
        IVisualizerEndpointResolver resolver, IHttpClientFactory http, IConfigStore config,
        ILogger<VisualizerControlService> logger)
    {
        _resolver = resolver;
        _http = http;
        _config = config;
        _logger = logger;
    }

    private static string DesiredKey(string sourceId) => "visualizer.desired." + sourceId;

    /// <summary>"on", "off", or null when the operator never chose.</summary>
    public string? DesiredFor(string sourceId)
    {
        var v = _config.Get(DesiredKey(sourceId), "");
        return v is "on" or "off" ? v : null;
    }

    public async Task<VisualizerPluginState> GetStateAsync(string sourceId, CancellationToken ct = default)
    {
        var ep = await _resolver.ResolveAsync(sourceId, ct);
        if (ep is null) return VisualizerPluginState.Unreachable;
        return await SendAsync(ep, HttpMethod.Get, "/health", ct);
    }

    /// <summary>Remembers the choice, then asks the plugin. <paramref name="now"/> skips the wait.</summary>
    public async Task<VisualizerPluginState> SetEnabledAsync(
        string sourceId, bool enabled, bool now = false, string? updatedBy = null, CancellationToken ct = default)
    {
        await _config.SetAsync(DesiredKey(sourceId), enabled ? "on" : "off", updatedBy, ct);
        var ep = await _resolver.ResolveAsync(sourceId, ct);
        if (ep is null) return VisualizerPluginState.Unreachable;
        return await SendAsync(ep, HttpMethod.Post, (enabled ? "/enable" : "/disable") + (now ? "?now=1" : ""), ct);
    }

    /// <summary>Re-applies the remembered choice if the plugin disagrees (for example after
    /// vLLM restarted and the plugin came back on). Graceful, like any other change.</summary>
    public async Task<VisualizerPluginState> ReconcileAsync(string sourceId, VisualizerPluginState state, CancellationToken ct = default)
    {
        if (!state.Reachable) return state;
        var desired = DesiredFor(sourceId);
        if (desired == "off" && state.Enabled && !state.PendingDisable)
            return await SetEnabledAsync(sourceId, false, ct: ct);
        if (desired == "on" && !state.Enabled && !state.PendingEnable)
            return await SetEnabledAsync(sourceId, true, ct: ct);
        return state;
    }

    private async Task<VisualizerPluginState> SendAsync(VisualizerEndpoint ep, HttpMethod method, string path, CancellationToken ct)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(CallTimeout);
            using var client = _http.CreateClient("visualizer");
            using var req = new HttpRequestMessage(method, new Uri(ep.Url, path));
            if (!string.IsNullOrEmpty(ep.Token))
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", ep.Token);
            using var resp = await client.SendAsync(req, cts.Token);
            if (!resp.IsSuccessStatusCode) return VisualizerPluginState.Unreachable;

            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(cts.Token));
            var root = doc.RootElement;
            bool Flag(string name) => root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;
            var reason = root.TryGetProperty("disabled_reason", out var r) && r.ValueKind == JsonValueKind.String ? r.GetString() : null;
            return new VisualizerPluginState(true, !Flag("disabled"), Flag("pending_disable"), Flag("pending_enable"), reason);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Visualizer plugin control call {Path} failed.", path);
            return VisualizerPluginState.Unreachable;
        }
    }
}
