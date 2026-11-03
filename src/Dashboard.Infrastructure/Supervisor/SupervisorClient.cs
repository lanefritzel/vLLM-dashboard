using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Dashboard.Core.Abstractions;
using Dashboard.Core.Supervisor;

namespace Dashboard.Infrastructure.Supervisor;

/// <summary>Typed HTTP client for a supervisor's admin API (P10a, doc 5). Each
/// call builds an absolute URL from the given <c>adminUrl</c> -- same
/// "named client + per-call absolute URL" pattern as <c>DockerOrchestrator</c>'s
/// own HTTP calls, since the target host varies per instance, not per
/// client registration.</summary>
public sealed class SupervisorClient : ISupervisorClient
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    private readonly IHttpClientFactory _httpFactory;

    public SupervisorClient(IHttpClientFactory httpFactory) => _httpFactory = httpFactory;

    private HttpClient NewClient() => _httpFactory.CreateClient("supervisor");

    private static string Url(string adminUrl, string path) => $"{adminUrl.TrimEnd('/')}{path}";

    private static HttpRequestMessage NewRequest(HttpMethod method, string url, string? token)
    {
        var request = new HttpRequestMessage(method, url);
        if (!string.IsNullOrEmpty(token))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return request;
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode)
            return;
        var body = await response.Content.ReadAsStringAsync(ct);
        throw new SupervisorApiException(
            (int)response.StatusCode, body, $"Supervisor returned HTTP {(int)response.StatusCode}: {body}");
    }

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response, CancellationToken ct)
    {
        await EnsureSuccessAsync(response, ct);
        var result = await response.Content.ReadFromJsonAsync<T>(JsonOptions, ct);
        return result ?? throw new SupervisorApiException((int)response.StatusCode, null, "Supervisor returned an empty body.");
    }

    public async Task<bool> HealthAsync(string adminUrl, CancellationToken ct = default)
    {
        try
        {
            using var client = NewClient();
            using var response = await client.GetAsync(Url(adminUrl, "/admin/health"), ct);
            return response.IsSuccessStatusCode;
        }
        catch (HttpRequestException)
        {
            return false;
        }
    }

    public async Task<SupervisorStatus> GetStatusAsync(string adminUrl, string token, CancellationToken ct = default)
    {
        using var client = NewClient();
        using var request = NewRequest(HttpMethod.Get, Url(adminUrl, "/admin/status"), token);
        using var response = await client.SendAsync(request, ct);
        return await ReadAsync<SupervisorStatus>(response, ct);
    }

    public async Task<string> DeployAsync(
        string adminUrl, string token, IReadOnlyList<string> argv, IReadOnlyDictionary<string, string>? env,
        string deploymentId, double healthTimeout, object? smoke = null, CancellationToken ct = default)
    {
        using var client = NewClient();
        using var request = NewRequest(HttpMethod.Post, Url(adminUrl, "/admin/deploy"), token);
        request.Content = JsonContent.Create(new
        {
            argv, env, deployment_id = deploymentId, health_timeout = healthTimeout, smoke,
        });
        using var response = await client.SendAsync(request, ct);
        var body = await ReadAsync<JsonElement>(response, ct);
        return body.GetProperty("deployment_id").GetString()!;
    }

    public async Task<IReadOnlyList<SupervisorDeploymentEvent>> GetEventsAsync(
        string adminUrl, string token, string deploymentId, int after = 0, CancellationToken ct = default)
    {
        using var client = NewClient();
        using var request = NewRequest(HttpMethod.Get, Url(adminUrl, $"/admin/deployments/{deploymentId}/events?after={after}"), token);
        using var response = await client.SendAsync(request, ct);
        return await ReadAsync<List<SupervisorDeploymentEvent>>(response, ct);
    }

    public async Task<SupervisorStopResult> StopAsync(string adminUrl, string token, CancellationToken ct = default)
    {
        using var client = NewClient();
        using var request = NewRequest(HttpMethod.Post, Url(adminUrl, "/admin/stop"), token);
        using var response = await client.SendAsync(request, ct);
        return await ReadAsync<SupervisorStopResult>(response, ct);
    }

    public async Task<SupervisorCancelResult> CancelAsync(string adminUrl, string token, string deploymentId, CancellationToken ct = default)
    {
        using var client = NewClient();
        using var request = NewRequest(HttpMethod.Post, Url(adminUrl, $"/admin/deployments/{deploymentId}/cancel"), token);
        using var response = await client.SendAsync(request, ct);
        return await ReadAsync<SupervisorCancelResult>(response, ct);
    }

    public async Task<string> RestartAsync(string adminUrl, string token, string? deploymentId = null, CancellationToken ct = default)
    {
        using var client = NewClient();
        using var request = NewRequest(HttpMethod.Post, Url(adminUrl, "/admin/restart"), token);
        request.Content = JsonContent.Create(new { deployment_id = deploymentId });
        using var response = await client.SendAsync(request, ct);
        var body = await ReadAsync<JsonElement>(response, ct);
        return body.GetProperty("deployment_id").GetString()!;
    }

    public async Task<SupervisorGpuInfo> GetGpuAsync(string adminUrl, string token, CancellationToken ct = default)
    {
        using var client = NewClient();
        using var request = NewRequest(HttpMethod.Get, Url(adminUrl, "/admin/gpu"), token);
        using var response = await client.SendAsync(request, ct);
        return await ReadAsync<SupervisorGpuInfo>(response, ct);
    }

    public async Task<string> GetAutoStartAsync(string adminUrl, string token, CancellationToken ct = default)
    {
        using var client = NewClient();
        using var request = NewRequest(HttpMethod.Get, Url(adminUrl, "/admin/settings"), token);
        using var response = await client.SendAsync(request, ct);
        var body = await ReadAsync<JsonElement>(response, ct);
        return body.GetProperty("AUTO_START").GetString()!;
    }

    /// <summary>JsonContent.Create(value) with no explicit options turns out to
    /// default to JsonSerializerOptions.Web in this runtime (confirmed via an
    /// isolated repro) -- i.e. camelCase -- not the policy-free
    /// JsonSerializerOptions.Default its own docs would suggest. That silently
    /// mangles an ALL-CAPS field name ("AUTO_START" arrived at the supervisor
    /// as "autO_START", a 422); every other JsonContent.Create call in this
    /// file happens to already use snake_case/lowercase field names, so this
    /// was invisible until the supervisor's own SettingsRequest model (the
    /// only all-caps field this client ever sends) exposed it. Pass this
    /// explicitly to every outgoing JsonContent.Create to guarantee property
    /// names reach the wire byte-for-byte as declared.</summary>
    private static readonly JsonSerializerOptions NoNamingPolicy = new();

    public async Task<string> SetAutoStartAsync(string adminUrl, string token, string autoStart, CancellationToken ct = default)
    {
        using var client = NewClient();
        using var request = NewRequest(HttpMethod.Put, Url(adminUrl, "/admin/settings"), token);
        request.Content = JsonContent.Create(new { AUTO_START = autoStart }, options: NoNamingPolicy);
        using var response = await client.SendAsync(request, ct);
        var body = await ReadAsync<JsonElement>(response, ct);
        return body.GetProperty("AUTO_START").GetString()!;
    }

    public async Task<SupervisorSmokeResult> SmokeTestAsync(string adminUrl, string token, CancellationToken ct = default)
    {
        using var client = NewClient();
        using var request = NewRequest(HttpMethod.Post, Url(adminUrl, "/admin/smoke-test"), token);
        using var response = await client.SendAsync(request, ct);
        return await ReadAsync<SupervisorSmokeResult>(response, ct);
    }

    public async Task<SupervisorDownloadInspectResult> InspectDownloadAsync(string adminUrl, string token, string repoId, CancellationToken ct = default)
    {
        using var client = NewClient();
        using var request = NewRequest(HttpMethod.Post, Url(adminUrl, "/admin/downloads/inspect"), token);
        request.Content = JsonContent.Create(new { repo_id = repoId });
        using var response = await client.SendAsync(request, ct);
        return await ReadAsync<SupervisorDownloadInspectResult>(response, ct);
    }

    public async Task<string> StartDownloadAsync(
        string adminUrl, string token, string repoId, string slug, string? filename = null, CancellationToken ct = default)
    {
        using var client = NewClient();
        using var request = NewRequest(HttpMethod.Post, Url(adminUrl, "/admin/downloads"), token);
        request.Content = JsonContent.Create(new { repo_id = repoId, slug, filename });
        using var response = await client.SendAsync(request, ct);
        var body = await ReadAsync<JsonElement>(response, ct);
        return body.GetProperty("download_id").GetString()!;
    }

    public async Task<SupervisorDownloadStatus> GetDownloadAsync(string adminUrl, string token, string downloadId, CancellationToken ct = default)
    {
        using var client = NewClient();
        using var request = NewRequest(HttpMethod.Get, Url(adminUrl, $"/admin/downloads/{downloadId}"), token);
        using var response = await client.SendAsync(request, ct);
        return await ReadAsync<SupervisorDownloadStatus>(response, ct);
    }

    public async Task CancelDownloadAsync(string adminUrl, string token, string downloadId, CancellationToken ct = default)
    {
        using var client = NewClient();
        using var request = NewRequest(HttpMethod.Delete, Url(adminUrl, $"/admin/downloads/{downloadId}"), token);
        using var response = await client.SendAsync(request, ct);
        await EnsureSuccessAsync(response, ct);
    }
}
