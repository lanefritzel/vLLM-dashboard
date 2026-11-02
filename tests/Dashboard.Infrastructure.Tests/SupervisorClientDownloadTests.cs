using Dashboard.Core.Supervisor;
using Dashboard.Infrastructure.Supervisor;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Dashboard.Infrastructure.Tests;

/// <summary>P13's "Add model" flow needs a .NET client for the supervisor's
/// download endpoints (P08 only ever built the Python side) -- these tests
/// prove <see cref="SupervisorClient"/>'s new methods call the right path/verb
/// and parse the wire shape correctly, against a small fake admin API (real
/// socket, not the full supervisor container -- no auth/download-manager
/// behavior under test here, just the HTTP client's own shape).</summary>
public class SupervisorClientDownloadTests : IAsyncDisposable
{
    private Microsoft.Extensions.Hosting.IHost? _fakeAdmin;

    public async ValueTask DisposeAsync()
    {
        if (_fakeAdmin is not null)
            await _fakeAdmin.StopAsync();
    }

    private async Task<string> StartFakeAdminAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var app = builder.Build();

        app.MapPost("/admin/downloads/inspect", async (HttpContext ctx) =>
        {
            ctx.Response.ContentType = "application/json";
            await ctx.Response.WriteAsync("""
                {"files":[{"path":"model.safetensors","size":123456},{"path":"config.json","size":512}],
                 "config":{"max_position_embeddings":8192},"free_bytes":999999999}
                """);
        });
        app.MapPost("/admin/downloads", async (HttpContext ctx) =>
        {
            ctx.Response.ContentType = "application/json";
            await ctx.Response.WriteAsync("""{"download_id":"dl-1"}""");
        });
        app.MapGet("/admin/downloads/{id}", async (HttpContext ctx, string id) =>
        {
            ctx.Response.ContentType = "application/json";
            await ctx.Response.WriteAsync("""
                {"status":"done","files_total":2,"files_done":2,"total_bytes":123968,"downloaded_bytes":123968,
                 "progress_pct":100.0,"speed_bps":5000.0,"eta_seconds":null,
                 "path":"/models/hf-cache/my-model","size_bytes":123968,
                 "config":{"max_position_embeddings":8192},"load_format":"auto"}
                """);
        });
        app.MapDelete("/admin/downloads/{id}", (string id) => Results.Ok(new { status = "cancelled" }));

        _fakeAdmin = app;
        await app.StartAsync();
        return app.Urls.First();
    }

    private static SupervisorClient NewClient()
    {
        var services = new ServiceCollection();
        services.AddHttpClient("supervisor");
        return new SupervisorClient(services.BuildServiceProvider().GetRequiredService<IHttpClientFactory>());
    }

    [Fact]
    public async Task InspectDownloadAsync_ParsesFilesConfigAndFreeBytes()
    {
        var adminUrl = await StartFakeAdminAsync();
        var client = NewClient();

        var result = await client.InspectDownloadAsync(adminUrl, "unused-token", "org/repo");

        Assert.Equal(2, result.Files.Count);
        Assert.Equal(123456, result.Files[0].Size);
        Assert.Equal(999999999, result.FreeBytes);
        Assert.NotNull(result.Config);
    }

    [Fact]
    public async Task StartDownloadAsync_ReturnsTheDownloadId()
    {
        var adminUrl = await StartFakeAdminAsync();
        var client = NewClient();

        var downloadId = await client.StartDownloadAsync(adminUrl, "unused-token", "org/repo", "my-model");

        Assert.Equal("dl-1", downloadId);
    }

    [Fact]
    public async Task GetDownloadAsync_ParsesProgressAndTerminalFields()
    {
        var adminUrl = await StartFakeAdminAsync();
        var client = NewClient();

        var status = await client.GetDownloadAsync(adminUrl, "unused-token", "dl-1");

        Assert.Equal("done", status.Status);
        Assert.Equal(100.0, status.ProgressPct);
        Assert.Equal("/models/hf-cache/my-model", status.Path);
        Assert.Equal(123968, status.SizeBytes);
        Assert.Equal("auto", status.LoadFormat);
    }

    [Fact]
    public async Task CancelDownloadAsync_DoesNotThrowOnSuccess()
    {
        var adminUrl = await StartFakeAdminAsync();
        var client = NewClient();

        await client.CancelDownloadAsync(adminUrl, "unused-token", "dl-1");
    }
}
