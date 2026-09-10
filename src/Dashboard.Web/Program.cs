using Dashboard.Application.Metrics;
using Dashboard.Core.Abstractions;
using Dashboard.Infrastructure.Identity;
using Dashboard.Infrastructure.Persistence;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.EntityFrameworkCore;
using Dashboard.Web.Components;
using Dashboard.Web.Components.Account;
using Dashboard.Web.Middleware;

var builder = WebApplication.CreateBuilder(args);

var connectionString = builder.Configuration.GetConnectionString("DefaultConnection")
    ?? Environment.GetEnvironmentVariable("DB_CONNECTION")
    ?? "Host=localhost;Port=5432;Database=dashboard;Username=dashboard;Password=dashboard";

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

builder.Services.AddHttpClient("metrics", c => c.Timeout = TimeSpan.FromSeconds(5));
builder.Services.AddHttpClient("docker", c => c.Timeout = TimeSpan.FromSeconds(30));
builder.Services.AddHttpClient("inference", c => c.Timeout = TimeSpan.FromSeconds(30));
// No client-side timeout: a proxied inference request (in particular a streaming SSE
// chat completion) has no natural fixed duration. Cancellation comes from the actual
// client disconnecting (HttpContext.RequestAborted), not a timer.
builder.Services.AddHttpClient("proxy", c => c.Timeout = Timeout.InfiniteTimeSpan);

// Cross-circuit shared UI state (which source the dashboard is showing).
builder.Services.AddSingleton<SourceSelection>();

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
builder.Services.AddSingleton<Dashboard.Application.Proxy.IModelProxyService, Dashboard.Application.Proxy.ModelProxyService>();

// Singleton; each opens a fresh AppDbContext per operation via IServiceScopeFactory
// (same pattern as ConfigStore/SampleStore) so concurrent calls from different
// components sharing one Blazor circuit never race on a single DbContext instance.
builder.Services.AddSingleton<ISourceCatalog, SourceCatalog>();
builder.Services.AddSingleton<IModelCatalog, ModelCatalog>();
builder.Services.AddSingleton<ILaunchSpecStore, LaunchSpecStore>();
builder.Services.AddSingleton<IMetricsHistory, MetricsHistory>();

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
app.MapHub<Dashboard.Application.Proxy.TrafficHub>("/traffic-hub").RequireAuthorization();
app.MapAdditionalIdentityEndpoints();

// Transparent proxy: inference clients hit this stable URL instead of a source's own
// vLLM address; it auto-swaps the backend model first if the request asks for a
// different one, then forwards. Deliberately NOT behind .RequireAuthorization() — this
// is a machine-to-machine endpoint for API clients, not a dashboard page, and they
// can't do the interactive cookie login the rest of this app uses.
app.Map("/proxy/{sourceId}/{**path}", async (HttpContext context, string sourceId, string? path, Dashboard.Application.Proxy.IModelProxyService proxy) =>
{
    await proxy.HandleAsync(context, sourceId, path ?? "");
});

app.Run();
