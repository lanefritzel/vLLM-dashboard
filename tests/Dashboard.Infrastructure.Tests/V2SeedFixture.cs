using Dashboard.Infrastructure.Identity;
using Dashboard.Infrastructure.Persistence.V2;

namespace Dashboard.Infrastructure.Tests;

/// <summary>Test-only seed data for the new schema (1 server, 1 instance, 2 models, 2
/// recipes), per P01a's Definition of done. Lives in the test project, not
/// Dashboard.Infrastructure, specifically so nothing in the production app can
/// reference or load it.</summary>
public static class V2SeedFixture
{
    public static async Task<Server> SeedAsync(AppDbContext db)
    {
        var server = new Server
        {
            Label = "primary",
            ModelsDir = "/models",
            GpuName = "Intel (12 GB)",
        };
        db.ServersV2.Add(server);
        await db.SaveChangesAsync();

        db.InstancesV2.Add(new Instance
        {
            ServerId = server.Id,
            Label = "main vLLM",
            ProxySlug = "main",
        });

        db.ModelsV2.AddRange(
            new Model { ServerId = server.Id, Name = "qwen3.8-vllm", Path = "/models/qwen3.8-int4" },
            new Model { ServerId = server.Id, Name = "llama-3.1-8b", Path = "/models/llama-3.1-8b", SourceType = "huggingface", RepoId = "meta-llama/Llama-3.1-8B" });

        db.RecipesV2.AddRange(
            new Recipe { ServerId = server.Id, Name = "default", ServedModelName = "main" },
            new Recipe { ServerId = server.Id, Name = "long-context", ServedModelName = "main", MaxModelLen = 131072 });

        await db.SaveChangesAsync();
        return server;
    }
}
