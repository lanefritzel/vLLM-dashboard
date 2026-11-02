using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;

namespace Dashboard.E2E.Tests;

/// <summary>Stands in for a real supervisor's admin API during the Add Model /
/// Hugging Face download E2E tests -- same technique as
/// Dashboard.Infrastructure.Tests/SupervisorClientDownloadTests.cs's fake admin,
/// but with a GET /admin/downloads/{id} that actually progresses across polls
/// (downloading once, then done) so AddModelDialog's progress bar has something
/// real to render before the "available" transition.</summary>
public sealed class FakeSupervisorAdmin : IAsyncDisposable
{
    private Microsoft.Extensions.Hosting.IHost? _host;
    private int _pollCount;

    public long FreeBytes { get; set; } = 10_000_000_000;
    public long FileSizeBytes { get; set; } = 1_000_500;

    public async Task<string> StartAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var app = builder.Build();

        app.MapPost("/admin/downloads/inspect", async (HttpContext ctx) =>
        {
            ctx.Response.ContentType = "application/json";
            await ctx.Response.WriteAsync($$"""
                {"files":[{"path":"model.safetensors","size":{{FileSizeBytes}}}],
                 "config":{"max_position_embeddings":4096},"free_bytes":{{FreeBytes}}}
                """);
        });

        app.MapPost("/admin/downloads", async (HttpContext ctx) =>
        {
            ctx.Response.ContentType = "application/json";
            await ctx.Response.WriteAsync("""{"download_id":"dl-e2e-1"}""");
        });

        app.MapGet("/admin/downloads/{id}", async (HttpContext ctx, string id) =>
        {
            ctx.Response.ContentType = "application/json";
            _pollCount++;
            if (_pollCount == 1)
            {
                await ctx.Response.WriteAsync($$"""
                    {"status":"downloading","files_total":1,"files_done":0,"total_bytes":{{FileSizeBytes}},
                     "downloaded_bytes":500000,"progress_pct":50.0,"speed_bps":250000.0,"eta_seconds":2}
                    """);
            }
            else
            {
                await ctx.Response.WriteAsync($$"""
                    {"status":"done","files_total":1,"files_done":1,"total_bytes":{{FileSizeBytes}},
                     "downloaded_bytes":{{FileSizeBytes}},"progress_pct":100.0,
                     "path":"/models/hf-cache/e2e-hf-model","size_bytes":{{FileSizeBytes}},
                     "config":{"max_position_embeddings":4096},"load_format":"auto"}
                    """);
            }
        });

        app.MapDelete("/admin/downloads/{id}", (string id) => Results.Ok(new { status = "cancelled" }));

        _host = app;
        await app.StartAsync();
        return app.Urls.First();
    }

    public async ValueTask DisposeAsync()
    {
        if (_host is not null)
            await _host.StopAsync();
    }
}
