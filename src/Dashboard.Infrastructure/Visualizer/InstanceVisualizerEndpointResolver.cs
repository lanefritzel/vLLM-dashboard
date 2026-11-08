using Dashboard.Infrastructure.Identity;
using Dashboard.Infrastructure.Persistence.V2;
using Dashboard.Infrastructure.Proxy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Dashboard.Infrastructure.Visualizer;

/// <summary>Plugin endpoint = the instance's own host (same derivation the proxy and the
/// metrics poller use) on the plugin's port.</summary>
public sealed class InstanceVisualizerEndpointResolver : IVisualizerEndpointResolver
{
    private readonly IServiceScopeFactory _scopes;
    private readonly VisualizerOptions _options;

    public InstanceVisualizerEndpointResolver(IServiceScopeFactory scopes, IOptions<VisualizerOptions> options)
    {
        _scopes = scopes;
        _options = options.Value;
    }

    public async Task<VisualizerEndpoint?> ResolveAsync(string sourceId, CancellationToken ct)
    {
        if (!int.TryParse(sourceId, out var instanceId)) return null;

        string? host = _options.HostOverride;
        if (string.IsNullOrWhiteSpace(host))
        {
            using var scope = _scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var instance = await db.InstancesV2.AsNoTracking().FirstOrDefaultAsync(i => i.Id == instanceId, ct);
            if (instance is null) return null;
            var baseUrl = ModelProxyService.BuildInferenceBaseUrl(instance);
            if (baseUrl is null) return null;
            host = new Uri(baseUrl).Host;
        }
        var token = string.IsNullOrWhiteSpace(_options.Token) ? null : _options.Token;
        return new VisualizerEndpoint(new UriBuilder("http", host, _options.Port, "/stream").Uri, token);
    }
}
