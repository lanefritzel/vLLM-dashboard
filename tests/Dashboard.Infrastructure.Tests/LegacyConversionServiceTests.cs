using System.Globalization;
using Dashboard.Core.Recipes;
using Dashboard.Infrastructure.Conversion;
using Dashboard.Infrastructure.Docker;
using Dashboard.Infrastructure.Identity;
using Microsoft.EntityFrameworkCore;

namespace Dashboard.Infrastructure.Tests;

/// <summary>P01b's Definition of done, against a real Postgres with both the legacy
/// and V2 schemas present (exactly P01a's migration state) -- the same state a real
/// upgrade runs the conversion routine against.</summary>
[Collection("Postgres")]
public class LegacyConversionServiceTests(PostgresFixture postgres)
{
    private async Task<AppDbContext> NewMigratedContextAsync()
    {
        var connectionString = await postgres.CreateFreshDatabaseAsync();
        var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(connectionString).Options);
        await db.Database.MigrateAsync();
        return db;
    }

    [Fact]
    public async Task LiveQwenFixture_ConvertsToExpectedRows_AndArgBuilderOutputMatchesTheOracle()
    {
        using var db = await NewMigratedContextAsync();
        var fixture = LegacyOracleFixture.Load("live-qwen.json");
        await LegacyOracleFixtureSeeder.SeedAsync(db, fixture);

        var report = await new LegacyConversionService(db).ConvertAsync();

        Assert.Equal(1, report.ServersConverted);
        Assert.Equal(1, report.InstancesConverted);
        Assert.Equal(3, report.ModelsConverted); // all 4 legacy models minus... wait: there are 3 in the fixture
        Assert.Equal(3, report.RecipesCreated);

        var server = await db.ServersV2.SingleAsync(s => s.Label == "tes.home");
        var instance = await db.InstancesV2.SingleAsync(i => i.ServerId == server.Id);
        Assert.Equal("tes-home", instance.ProxySlug);
        Assert.Null(instance.AdminUrl);

        var model = await db.ModelsV2.SingleAsync(m => m.ServerId == server.Id && m.Name == "qwen3.8-vllm");
        Assert.Equal("available", model.Status);
        Assert.Equal("local", model.SourceType);
        Assert.Equal("/media/external6tb/llm-models/hf-cache/Qwen3.8-27B-INT4", model.Path);

        var neverDownloaded = await db.ModelsV2.Where(m => m.ServerId == server.Id && m.Name != "qwen3.8-vllm").ToListAsync();
        Assert.All(neverDownloaded, m => Assert.Equal("download_failed", m.Status));
        Assert.Contains(report.Notes, n => n.Code == "model_never_downloaded");
        Assert.Contains(report.Notes, n => n.Code == "options_without_dial" && n.Message.Contains("mm-encoder-tp-mode"));

        var recipe = await db.RecipesV2.SingleAsync(r => r.ServerId == server.Id && r.Name == "qwen3.8-vllm (migrated)");
        Assert.Equal("qwen3.8-vllm", recipe.ServedModelName);
        Assert.Equal("float16", recipe.Dtype);
        Assert.Equal(0.80f, recipe.GpuMemoryUtilization, 2);
        Assert.Equal(131072, recipe.MaxModelLen);
        Assert.True(recipe.EnableAutoToolChoice);
        Assert.Equal("qwen3_coder", recipe.ToolCallParser);
        Assert.Equal("qwen3", recipe.ReasoningParser);
        // Not stored in the live row's options at all -- stays at the recipe's own
        // defaults, not silently back-filled from the live compose reference.
        Assert.False(recipe.EnforceEager);
        Assert.Equal("auto", recipe.KvCacheDtype);

        // The golden comparison: ArgBuilder on the CONVERTED recipe vs the OLD
        // app's own oracle (DockerOrchestrator.BuildCreatePayload) for the
        // equivalent legacy row. A SUBSET match, not set equality (both points
        // owner-confirmed 2026-10-31): excludes the container model path (old and
        // new resolve it via genuinely different, redesigned schemes) and the
        // port (instance-owned on the new side, not recipe-owned); and the new
        // side may have EXTRA flags the oracle doesn't -- e.g.
        // --max-num-batched-tokens/--block-size, which the new recipe dial table
        // always emits (with its own default) even when the legacy row never
        // configured them at all. Every flag the oracle DOES set must still match
        // exactly -- that's the actual "nothing was lost in conversion" guarantee.
        var oracle = BuildOracleForLiveQwen();
        var newArgs = ArgBuilder.Build(
            model: new RecipeModel { Path = model.Path, LoadFormat = model.LoadFormat, Status = model.Status },
            recipe: ToArgBuilderRecipe(recipe),
            instance: new RecipeInstance { Port = instance.Port },
            server: new RecipeServer { ModelsDir = server.ModelsDir });

        var oracleFlags = ToFlagDictionary(oracle.Argv, skipLeadingTokens: 2); // skip "serve", "<path>"
        var newFlags = ToFlagDictionary(newArgs.Argv, skipLeadingTokens: 3);   // skip "vllm", "serve", "<path>"
        newFlags.Remove("--port"); // instance-owned, injected last on the new side -- not part of this comparison

        var missingFromNew = oracleFlags.Keys.Except(newFlags.Keys).ToList();
        Assert.Empty(missingFromNew);
        foreach (var (flag, oracleValue) in oracleFlags)
        {
            // The legacy row's stored string is ".80" (a historical formatting
            // quirk of how it was typed into the old UI) vs. the new side's
            // normalized "0.8" -- numerically identical, so compare as numbers
            // for this one flag rather than demanding the old app's odd literal
            // formatting survive the round trip.
            if (flag == "--gpu-memory-utilization")
            {
                Assert.Equal(double.Parse(oracleValue!, CultureInfo.InvariantCulture), double.Parse(newFlags[flag]!, CultureInfo.InvariantCulture));
                continue;
            }
            Assert.Equal(oracleValue, newFlags[flag]);
        }
    }

    [Fact]
    public async Task OllamaSource_IsDroppedAndAppearsInTheReport()
    {
        using var db = await NewMigratedContextAsync();
        var fixture = LegacyOracleFixture.Load("ollama-source.json");
        await LegacyOracleFixtureSeeder.SeedAsync(db, fixture);

        var report = await new LegacyConversionService(db).ConvertAsync();

        Assert.Equal(0, report.InstancesConverted);
        Assert.Equal(0, report.ModelsConverted);
        Assert.Contains("Home Ollama", report.OllamaSourcesDropped);
        Assert.Contains(report.Notes, n => n.Code == "ollama_source_dropped");
        Assert.Equal(0, await db.InstancesV2.CountAsync());
        Assert.Equal(0, await db.ModelsV2.CountAsync());
    }

    [Fact]
    public async Task TwoSourcesOnOnePhysicalServer_ConvertToOneServerRowAndTwoInstances()
    {
        using var db = await NewMigratedContextAsync();
        var fixture = LegacyOracleFixture.Load("two-sources-one-server.json");
        await LegacyOracleFixtureSeeder.SeedAsync(db, fixture);

        var report = await new LegacyConversionService(db).ConvertAsync();

        Assert.Equal(1, report.ServersConverted);
        Assert.Equal(2, report.InstancesConverted);

        var server = await db.ServersV2.SingleAsync(s => s.Label == "multi-gpu-box");
        var instances = await db.InstancesV2.Where(i => i.ServerId == server.Id).ToListAsync();
        Assert.Equal(2, instances.Count);
        Assert.Contains(instances, i => i.ProxySlug == "gpu0-main");
        Assert.Contains(instances, i => i.ProxySlug == "gpu1-coder");
        Assert.All(instances, i => Assert.Null(i.AdminUrl));
    }

    [Fact]
    public async Task ConvertedInstances_HaveNoDeployments()
    {
        using var db = await NewMigratedContextAsync();
        await LegacyOracleFixtureSeeder.SeedAsync(db, LegacyOracleFixture.Load("live-qwen.json"));

        await new LegacyConversionService(db).ConvertAsync();

        Assert.Equal(0, await db.DeploymentsV2.CountAsync());
    }

    [Fact]
    public async Task RunningTwice_IsIdempotent_SecondRunChangesNothing()
    {
        using var db = await NewMigratedContextAsync();
        await LegacyOracleFixtureSeeder.SeedAsync(db, LegacyOracleFixture.Load("live-qwen.json"));

        var first = await new LegacyConversionService(db).ConvertAsync();
        Assert.True(first.ServersConverted + first.InstancesConverted + first.ModelsConverted + first.RecipesCreated > 0);

        var second = await new LegacyConversionService(db).ConvertAsync();

        Assert.Equal(0, second.ServersConverted);
        Assert.Equal(0, second.InstancesConverted);
        Assert.Equal(0, second.ModelsConverted);
        Assert.Equal(0, second.RecipesCreated);
        Assert.Equal(1, await db.ServersV2.CountAsync());
        Assert.Equal(1, await db.InstancesV2.CountAsync());
        Assert.Equal(3, await db.ModelsV2.CountAsync());
        Assert.Equal(3, await db.RecipesV2.CountAsync());
    }

    [Fact]
    public async Task IdentityRows_AreUnchangedAfterConversion()
    {
        using var db = await NewMigratedContextAsync();
        await LegacyOracleFixtureSeeder.SeedAsync(db, LegacyOracleFixture.Load("live-qwen.json"));
        var user = new ApplicationUser { UserName = "owner", NormalizedUserName = "OWNER", Email = "owner@example.com" };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        var before = await db.Users.AsNoTracking().SingleAsync(u => u.Id == user.Id);

        await new LegacyConversionService(db).ConvertAsync();

        var after = await db.Users.AsNoTracking().SingleAsync(u => u.Id == user.Id);
        Assert.Equal(before.UserName, after.UserName);
        Assert.Equal(before.Email, after.Email);
        Assert.Equal(before.PasswordHash, after.PasswordHash);
        Assert.Equal(before.SecurityStamp, after.SecurityStamp);
    }

    private static Recipe ToArgBuilderRecipe(Persistence.V2.Recipe recipe) => new()
    {
        ServedModelName = recipe.ServedModelName,
        MaxModelLen = recipe.MaxModelLen,
        GpuMemoryUtilization = recipe.GpuMemoryUtilization,
        KvCacheDtype = recipe.KvCacheDtype,
        Dtype = recipe.Dtype,
        EnforceEager = recipe.EnforceEager,
        MaxNumBatchedTokens = recipe.MaxNumBatchedTokens,
        BlockSize = recipe.BlockSize,
        SpeculativeDecoding = recipe.SpeculativeDecoding,
        SpeculativeTokens = recipe.SpeculativeTokens,
        EnableAutoToolChoice = recipe.EnableAutoToolChoice,
        ToolCallParser = recipe.ToolCallParser,
        ReasoningParser = recipe.ReasoningParser,
        ExtraArgs = System.Text.Json.JsonSerializer.Deserialize<List<Dictionary<string, string?>>>(recipe.ExtraArgs)!
            .Select(d => new RecipeExtraArg(d["key"]!, d.GetValueOrDefault("value")))
            .ToList(),
    };

    /// <summary>The oracle: what the OLD app's own argv builder produces for the
    /// live qwen row's actual stored data (not the live compose file, which the
    /// page itself says may differ -- see plan/conversion-mapping.md).</summary>
    private static Core.Recipes.ArgBuilderResult BuildOracleForLiveQwen()
    {
        var model = new Core.Models.ModelInfo
        {
            Name = "qwen3.8-vllm",
            ModelSource = Core.Models.ModelSource.LocalPath,
            ModelPathOrRepoId = "/media/external6tb/llm-models/hf-cache/Qwen3.8-27B-INT4",
            Options = new Dictionary<string, string>
            {
                ["dtype"] = "float16",
                ["enable-auto-tool-choice"] = "true",
                ["tool-call-parser"] = "qwen3_coder",
                ["reasoning-parser"] = "qwen3",
                ["max-model-len"] = "131072",
                ["gpu-memory-utilization"] = ".80",
                ["mm-encoder-tp-mode"] = "data",
            },
        };
        var server = new Core.Models.PhysicalServerInfo
        {
            Id = Guid.NewGuid().ToString(), Label = "tes.home", DockerHost = "tcp://tes.home:2375",
            GpuVendor = Core.Models.GpuVendor.Intel,
        };
        var spec = new Core.Models.LaunchSpec();

        var payload = (Dictionary<string, object>)DockerOrchestrator.BuildCreatePayload(spec, model, server, new NoneGpuProvisioner());
        var cmd = ((List<string>)payload["Cmd"]).ToArray();
        var env = payload.TryGetValue("Env", out var envObj)
            ? ((string[])envObj).Select(kv => kv.Split('=', 2)).ToDictionary(p => p[0], p => p[1])
            : new Dictionary<string, string>();

        return new Core.Recipes.ArgBuilderResult { Argv = cmd, Env = env };
    }

    private static Dictionary<string, string?> ToFlagDictionary(IReadOnlyList<string> argv, int skipLeadingTokens)
    {
        var result = new Dictionary<string, string?>(StringComparer.Ordinal);
        for (var i = skipLeadingTokens; i < argv.Count; i++)
        {
            var flag = argv[i];
            var hasValue = i + 1 < argv.Count && !argv[i + 1].StartsWith("--", StringComparison.Ordinal);
            result[flag] = hasValue ? argv[++i] : null;
        }
        return result;
    }
}
