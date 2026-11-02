using Dashboard.Application.Metrics;
using Dashboard.Core.Abstractions;
using Dashboard.Infrastructure.Identity;
using Dashboard.Infrastructure.Persistence;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Dashboard.Web.Components;
using Dashboard.Web.Components.Account;
using Dashboard.Web.Middleware;

var builder = WebApplication.CreateBuilder(args);

var connectionString = builder.Configuration.GetConnectionString("DefaultConnection")
    ?? Environment.GetEnvironmentVariable("DB_CONNECTION")
    ?? "Host=localhost;Port=5432;Database=dashboard;Username=dashboard;Password=dashboard";

// Durable log capture: docker logs is tied to the container's lifetime and is wiped
// on every redeploy — a real incident needed evidence to survive that. DbLogger writes
// into this channel (never blocking/awaiting), DbLogWriterService batches it into the
// database in the background, and RollupService prunes it on the same retention
// window as metrics, so this never grows unbounded.
var dbLogChannel = System.Threading.Channels.Channel.CreateUnbounded<Dashboard.Infrastructure.Logging.AppLogRecord>();
builder.Services.AddSingleton(dbLogChannel.Reader);
builder.Logging.AddProvider(new Dashboard.Infrastructure.Logging.DbLoggerProvider(
    dbLogChannel.Writer, minLevelForApp: LogLevel.Information, minLevelForOther: LogLevel.Warning));
builder.Services.AddHostedService<Dashboard.Infrastructure.Logging.DbLogWriterService>();

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

builder.Services.AddCascadingAuthenticationState();
builder.Services.AddScoped<IdentityRedirectManager>();
builder.Services.AddScoped<AuthenticationStateProvider, IdentityRevalidatingAuthenticationStateProvider>();

builder.Services.AddAuthentication(options =>
{
    options.DefaultScheme = IdentityConstants.ApplicationScheme;
    options.DefaultSignInScheme = IdentityConstants.ExternalScheme;
})
.AddIdentityCookies();

var dataProtectionDirectory = Environment.GetEnvironmentVariable("DATAPROTECTION_KEYS_DIR")
    ?? "/data/dataprotection";
try
{
    Directory.CreateDirectory(dataProtectionDirectory);
    builder.Services.AddDataProtection()
        .PersistKeysToFileSystem(new System.IO.DirectoryInfo(dataProtectionDirectory))
        .SetApplicationName("vllm-dashboard");
}
catch
{
    builder.Services.AddDataProtection().SetApplicationName("vllm-dashboard");
}

builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseNpgsql(connectionString));

// ---- Metrics / live telemetry wiring --------------------------------------
builder.Services.AddSignalR();
// Applies to every hub, including Blazor Server's own circuit connection: pinging
// more often gives an intermediate router/firewall's idle-connection reaping less
// opportunity to kill a persistent WebSocket that's just sitting quiet between
// events — seen in practice as the whole circuit (and any open hub, e.g. Live
// Traffic) dropping and reconnecting every 20-30s on some home networks, with
// nothing logged server-side since the connection dies in the network path, not
// in the app. Shorter pings don't fix a hard network-level timeout below this
// interval, but they meaningfully narrow the exposure window.
builder.Services.Configure<HubOptions>(o =>
{
    o.KeepAliveInterval = TimeSpan.FromSeconds(5);
    o.ClientTimeoutInterval = TimeSpan.FromSeconds(20);
});

builder.Services.AddHttpClient("metrics", c => c.Timeout = TimeSpan.FromSeconds(5));
builder.Services.AddHttpClient("docker", c => c.Timeout = TimeSpan.FromSeconds(30));
// Docker's log-follow endpoint is a long-lived streaming connection, not a normal
// request/response call — needs the same infinite timeout as the "proxy" client.
builder.Services.AddHttpClient("docker-logs", c => c.Timeout = Timeout.InfiniteTimeSpan);
// Same story for an image pull: Docker streams progress over one open connection
// until the pull actually finishes, which for a multi-GB image can easily take
// minutes — the general "docker" client's 30s timeout would (and did, in testing)
// kill the request outright while the pull was still genuinely progressing.
builder.Services.AddHttpClient("docker-pull", c => c.Timeout = Timeout.InfiniteTimeSpan);
builder.Services.AddHttpClient("inference", c => c.Timeout = TimeSpan.FromSeconds(30));
// The supervisor's own admin API (P10a) -- short timeout per call; a real
// deploy taking longer than this is tracked via polling (events?after=),
// never by holding one request open the way docker-pull's streaming calls do.
builder.Services.AddHttpClient("supervisor", c => c.Timeout = TimeSpan.FromSeconds(30));
builder.Services.AddHttpClient("huggingface", c =>
{
    c.Timeout = TimeSpan.FromSeconds(15);
    c.DefaultRequestHeaders.UserAgent.ParseAdd("vLLM-Dashboard/1.0");
});
builder.Services.AddSingleton<Dashboard.Core.Abstractions.IHuggingFaceModelLookup, Dashboard.Application.HuggingFace.HuggingFaceModelLookupService>();
// No client-side timeout: a proxied inference request (in particular a streaming SSE
// chat completion) has no natural fixed duration. Cancellation comes from the actual
// client disconnecting (HttpContext.RequestAborted), not a timer.
builder.Services.AddHttpClient("proxy", c => c.Timeout = Timeout.InfiniteTimeSpan);

// Cross-circuit shared UI state (which source the dashboard is showing).
builder.Services.AddSingleton<SourceSelection>();

// Themed confirm/alert dialogs — scoped, not singleton: each circuit's pending
// dialog (if any) is its own, never shared across users.
builder.Services.AddScoped<Dashboard.Web.Components.Shared.IDialogService, Dashboard.Web.Components.Shared.DialogService>();

// Singletons (no DB scope captured).
builder.Services.AddSingleton<IConfigStore, ConfigStore>();
builder.Services.AddSingleton<ISampleStore, SampleStore>();
builder.Services.AddSingleton<IMetricsAggregator, Dashboard.Infrastructure.Scrapers.MetricsAggregator>();
builder.Services.AddSingleton<IBroadcastThrottler, Dashboard.Application.Metrics.MetricsBroadcastThrottler>();
builder.Services.AddSingleton<IMetricsService, Dashboard.Application.Metrics.MetricsService>();
builder.Services.AddSingleton<IMetricsSourceFactory, Dashboard.Infrastructure.Scrapers.MetricsSourceFactory>();
builder.Services.AddSingleton<IInferenceProbe, Dashboard.Application.Metrics.InferenceProbeService>();
builder.Services.AddSingleton<IGpuProvisionerFactory, Dashboard.Infrastructure.Docker.GpuProvisionerFactory>();
builder.Services.AddSingleton<IDockerOrchestrator, Dashboard.Infrastructure.Docker.DockerOrchestrator>();
builder.Services.AddSingleton<Dashboard.Infrastructure.Proxy.IModelProxyService, Dashboard.Infrastructure.Proxy.ModelProxyService>();
builder.Services.AddSingleton<Dashboard.Infrastructure.Proxy.IInFlightTrafficTracker, Dashboard.Infrastructure.Proxy.InFlightTrafficTracker>();
builder.Services.AddSingleton<Dashboard.Application.Logs.ILogStreamManager, Dashboard.Application.Logs.LogStreamManager>();

// Singleton; each opens a fresh AppDbContext per operation via IServiceScopeFactory
// (same pattern as ConfigStore/SampleStore) so concurrent calls from different
// components sharing one Blazor circuit never race on a single DbContext instance.
builder.Services.AddSingleton<ISourceCatalog, SourceCatalog>();
builder.Services.AddSingleton<Dashboard.Core.Abstractions.IPhysicalServerCatalog, Dashboard.Infrastructure.Persistence.PhysicalServerCatalog>();
builder.Services.AddSingleton<IModelCatalog, ModelCatalog>();
builder.Services.AddSingleton<ILaunchSpecStore, LaunchSpecStore>();
builder.Services.AddSingleton<IMetricsHistory, MetricsHistory>();

// Read-only write guard (P12) -- registered before the V2 services below
// since they all depend on it.
builder.Services.AddSingleton<Dashboard.Core.Abstractions.IReadOnlyGuard, Dashboard.Infrastructure.Persistence.V2.ReadOnlyGuard>();
builder.Services.AddHostedService<Dashboard.Infrastructure.Persistence.V2.DatabaseHealthMonitorService>();

// New-schema (V2) service layer (P09b) -- same singleton + per-operation-scope
// pattern as the legacy catalogs above.
builder.Services.AddSingleton<Dashboard.Infrastructure.Persistence.V2.IServerService, Dashboard.Infrastructure.Persistence.V2.ServerService>();
builder.Services.AddSingleton<Dashboard.Infrastructure.Persistence.V2.IInstanceService, Dashboard.Infrastructure.Persistence.V2.InstanceService>();
builder.Services.AddSingleton<Dashboard.Infrastructure.Persistence.V2.IModelService, Dashboard.Infrastructure.Persistence.V2.ModelService>();
builder.Services.AddSingleton<Dashboard.Infrastructure.Persistence.V2.IRecipeService, Dashboard.Infrastructure.Persistence.V2.RecipeService>();
builder.Services.AddSingleton<Dashboard.Infrastructure.Persistence.V2.IDeploymentService, Dashboard.Infrastructure.Persistence.V2.DeploymentService>();
builder.Services.AddSingleton<Dashboard.Core.Abstractions.ISupervisorTokenProvider, Dashboard.Infrastructure.Persistence.V2.ConfigSupervisorTokenProvider>();

// Swap orchestration (P10a).
builder.Services.AddSingleton<Dashboard.Core.Abstractions.ISupervisorClient, Dashboard.Infrastructure.Supervisor.SupervisorClient>();
builder.Services.AddSingleton<Dashboard.Core.Abstractions.ISwapService, Dashboard.Infrastructure.Persistence.V2.SwapService>();

// Event receiver + catch-up (P10b).
builder.Services.AddSingleton<Dashboard.Core.Abstractions.IDeploymentEventApplier, Dashboard.Infrastructure.Persistence.V2.DeploymentEventApplier>();
builder.Services.AddSingleton<Dashboard.Infrastructure.Persistence.V2.SupervisorEventReceiver>();
builder.Services.AddHostedService<Dashboard.Infrastructure.Persistence.V2.DeploymentCatchUpService>();

// Boot reconcile + degradation (P12).
builder.Services.AddSingleton<Dashboard.Infrastructure.Persistence.V2.BootReconciler>();
builder.Services.AddSingleton<Dashboard.Core.Abstractions.IBootReconciler>(sp => sp.GetRequiredService<Dashboard.Infrastructure.Persistence.V2.BootReconciler>());
builder.Services.AddSingleton<Dashboard.Core.Abstractions.IReconcileActions>(sp => sp.GetRequiredService<Dashboard.Infrastructure.Persistence.V2.BootReconciler>());
builder.Services.AddSingleton<Dashboard.Core.Abstractions.IMinimalDockerClient, Dashboard.Infrastructure.Docker.MinimalDockerClient>();

// Hosted services. Order matters: hosted services start in registration order,
// so the DB initializer (migrations + seed) must start before the metrics
// polling and rollup loops that depend on the schema.
builder.Services.AddHostedService<Dashboard.Web.DbInitializerHostedService>();
builder.Services.AddHostedService<MetricsPollingService>();
builder.Services.AddHostedService<RollupService>();

builder.Services.AddIdentityCore<ApplicationUser>(options =>
{
    options.SignIn.RequireConfirmedAccount = false;
    options.Password.RequiredLength = 6;
    options.Password.RequireNonAlphanumeric = false;
    options.Password.RequireUppercase = false;
    options.Password.RequireLowercase = false;
    options.Password.RequireDigit = false;
})
.AddRoles<IdentityRole>()
.AddEntityFrameworkStores<AppDbContext>()
.AddSignInManager()
.AddDefaultTokenProviders();

builder.Services.AddSingleton<IEmailSender<ApplicationUser>, IdentityNoOpEmailSender>();

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.UseMigrationsEndPoint();
}
else
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
}
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);

app.UseAntiforgery();
app.UseAuthentication();
app.UseAuthorization();
app.UsePendingUserGuard();

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.MapHub<MetricsHub>("/metrics-hub").RequireAuthorization();
app.MapHub<Dashboard.Infrastructure.Proxy.TrafficHub>("/traffic-hub").RequireAuthorization();
app.MapHub<Dashboard.Application.Logs.LogsHub>("/logs-hub").RequireAuthorization();
app.MapHub<Dashboard.Infrastructure.Events.DeploymentHub>("/deployment-hub").RequireAuthorization();

// Supervisor event receiver (P10b) -- machine-to-machine, bearer-token
// authenticated, NOT under /proxy and NOT the cookie login (doc 5): it's
// reachable on the published port, so the per-instance token check (not
// RequireAuthorization, which is the dashboard's own cookie auth) is the
// only protection.
app.MapPost("/internal/supervisor-events", async (
    HttpContext context,
    Dashboard.Core.Supervisor.SupervisorPushEvent evt,
    Dashboard.Infrastructure.Persistence.V2.SupervisorEventReceiver receiver) =>
{
    var authHeader = context.Request.Headers.Authorization.ToString();
    var result = await receiver.HandleAsync(evt, authHeader, context.RequestAborted);
    return result switch
    {
        Dashboard.Infrastructure.Persistence.V2.SupervisorEventReceiverResult.Ok => Results.Ok(),
        Dashboard.Infrastructure.Persistence.V2.SupervisorEventReceiverResult.BadRequest => Results.BadRequest(),
        Dashboard.Infrastructure.Persistence.V2.SupervisorEventReceiverResult.NotFound => Results.NotFound(),
        _ => Results.Unauthorized(),
    };
});
app.MapAdditionalIdentityEndpoints();

// Transparent proxy (P11): inference clients hit this stable URL instead of an
// instance's own vLLM address. A thin mirror -- no auto-swap, no idle-unload, no
// preload; a request for a registered-but-not-active model gets a helpful error
// instead of a silent swap (the dashboard asks the supervisor to change what's
// running, via P10a's ISwapService; nothing on this request path ever does).
// Deliberately NOT behind .RequireAuthorization() — this is a machine-to-machine
// endpoint for API clients, not a dashboard page, and they can't do the
// interactive cookie login the rest of this app uses. The route segment is
// always the instance's proxy_slug (invariant: /proxy/<slug>/v1 never changes).
app.Map("/proxy/{slug}/{**path}", async (HttpContext context, string slug, string? path, Dashboard.Infrastructure.Proxy.IModelProxyService proxy) =>
{
    await proxy.HandleAsync(context, slug, path ?? "");
});

app.Run();
