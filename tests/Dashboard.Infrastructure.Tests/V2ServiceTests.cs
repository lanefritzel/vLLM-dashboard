using Dashboard.Infrastructure.Identity;
using Dashboard.Infrastructure.Persistence.V2;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Dashboard.Infrastructure.Tests;

/// <summary>P09b's Definition of done: CRUD round-trips for all five entities
/// through the service layer (not raw DbContext calls), args_snapshot immutable
/// at runtime, the partial unique index surfacing as a friendly error, and a
/// legacy/converted-shaped row validating without throwing.</summary>
[Collection("Postgres")]
public class V2ServiceTests(PostgresFixture postgres)
{
    private async Task<IServiceScopeFactory> NewMigratedScopeFactoryAsync()
    {
        var connectionString = await postgres.CreateFreshDatabaseAsync();
        var services = new ServiceCollection();
        services.AddDbContext<AppDbContext>(o => o.UseNpgsql(connectionString));
        var provider = services.BuildServiceProvider();

        using var scope = provider.CreateScope();
        await scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.MigrateAsync();
        return provider.GetRequiredService<IServiceScopeFactory>();
    }

    private static async Task<Server> SeedServerAsync(IServiceScopeFactory scopeFactory, string? gpuName = "Intel (12 GB)")
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var server = new Server { Label = "primary", ModelsDir = "/models", GpuName = gpuName };
        db.ServersV2.Add(server);
        await db.SaveChangesAsync();
        return server;
    }

    // --- CRUD round-trips ---

    [Fact]
    public async Task ServerService_CrudRoundTrips()
    {
        var scopeFactory = await NewMigratedScopeFactoryAsync();
        var service = new ServerService(scopeFactory, new ReadOnlyGuard());

        var created = await service.CreateAsync(new Server { Label = "primary", ModelsDir = "/models" });
        Assert.True(created.Id > 0);

        var fetched = await service.GetAsync(created.Id);
        Assert.Equal("primary", fetched!.Label);

        fetched.GpuName = "Intel Arc A770";
        var updated = await service.UpdateAsync(fetched);
        Assert.Equal("Intel Arc A770", updated.GpuName);

        await service.DeleteAsync(created.Id);
        Assert.Null(await service.GetAsync(created.Id));
    }

    [Fact]
    public async Task InstanceService_CrudRoundTrips()
    {
        var scopeFactory = await NewMigratedScopeFactoryAsync();
        var server = await SeedServerAsync(scopeFactory);
        var service = new InstanceService(scopeFactory, new ReadOnlyGuard());

        var created = await service.CreateAsync(new Instance { ServerId = server.Id, Label = "main vLLM", ProxySlug = "main" });
        Assert.True(created.Id > 0);

        var fetched = await service.GetAsync(created.Id);
        Assert.Equal("main", fetched!.ProxySlug);

        fetched.ProxySlug = "main-v2";
        var (updated, slugChanged) = await service.UpdateAsync(fetched);
        Assert.True(slugChanged);
        Assert.Equal("main-v2", updated.ProxySlug);

        var list = await service.ListByServerAsync(server.Id);
        Assert.Single(list);

        await service.DeleteAsync(created.Id);
        Assert.Null(await service.GetAsync(created.Id));
    }

    [Fact]
    public async Task ModelService_CrudRoundTrips()
    {
        var scopeFactory = await NewMigratedScopeFactoryAsync();
        var server = await SeedServerAsync(scopeFactory);
        var service = new ModelService(scopeFactory, new ReadOnlyGuard());

        var created = await service.CreateAsync(new Model { ServerId = server.Id, Name = "qwen", Path = "/models/hf-cache/qwen" });
        Assert.True(created.Id > 0);

        var fetched = await service.GetAsync(created.Id);
        Assert.Equal("/models/hf-cache/qwen", fetched!.Path);

        fetched.Status = "downloading";
        var updated = await service.UpdateAsync(fetched);
        Assert.Equal("downloading", updated.Status);

        await service.DeleteAsync(created.Id);
        Assert.Null(await service.GetAsync(created.Id));
    }

    [Fact]
    public async Task RecipeService_CrudRoundTrips()
    {
        var scopeFactory = await NewMigratedScopeFactoryAsync();
        var server = await SeedServerAsync(scopeFactory, gpuName: "NVIDIA RTX 4090"); // non-Intel -- no dtype rule in the way
        var service = new RecipeService(scopeFactory, new ReadOnlyGuard());

        var created = await service.CreateAsync(new Recipe { ServerId = server.Id, Name = "default", ServedModelName = "main" });
        Assert.True(created.Id > 0);

        var fetched = await service.GetAsync(created.Id);
        Assert.Equal("default", fetched!.Name);

        fetched.MaxModelLen = 32768;
        var updated = await service.UpdateAsync(fetched);
        Assert.Equal(32768, updated.MaxModelLen);

        await service.DeleteAsync(created.Id);
        Assert.Null(await service.GetAsync(created.Id));
    }

    [Fact]
    public async Task DeploymentService_CrudRoundTrips()
    {
        var scopeFactory = await NewMigratedScopeFactoryAsync();
        var server = await SeedServerAsync(scopeFactory, gpuName: "NVIDIA RTX 4090");
        var instanceService = new InstanceService(scopeFactory, new ReadOnlyGuard());
        var modelService = new ModelService(scopeFactory, new ReadOnlyGuard());
        var recipeService = new RecipeService(scopeFactory, new ReadOnlyGuard());
        var deploymentService = new DeploymentService(scopeFactory, new ReadOnlyGuard());

        var instance = await instanceService.CreateAsync(new Instance { ServerId = server.Id, Label = "main", ProxySlug = "main" });
        var model = await modelService.CreateAsync(new Model { ServerId = server.Id, Name = "qwen", Path = "/models/hf-cache/qwen" });
        var recipe = await recipeService.CreateAsync(new Recipe { ServerId = server.Id, Name = "default", ServedModelName = "main" });

        var created = await deploymentService.CreateAsync(new Deployment
        {
            InstanceId = instance.Id, ModelId = model.Id, RecipeId = recipe.Id,
            ArgsSnapshot = """["vllm","serve","/models/hf-cache/qwen"]""", Status = "pending",
        });
        Assert.True(created.Id > 0);

        var fetched = await deploymentService.GetAsync(created.Id);
        Assert.Equal("pending", fetched!.Status);

        var list = await deploymentService.ListByInstanceAsync(instance.Id);
        Assert.Single(list);
    }

    // --- args_snapshot immutability ---

    [Fact]
    public async Task DeploymentService_ArgsSnapshotIsFrozenAtCreateTime_TransitionNeverTouchesIt()
    {
        var scopeFactory = await NewMigratedScopeFactoryAsync();
        var server = await SeedServerAsync(scopeFactory, gpuName: "NVIDIA RTX 4090");
        var instanceService = new InstanceService(scopeFactory, new ReadOnlyGuard());
        var modelService = new ModelService(scopeFactory, new ReadOnlyGuard());
        var recipeService = new RecipeService(scopeFactory, new ReadOnlyGuard());
        var deploymentService = new DeploymentService(scopeFactory, new ReadOnlyGuard());

        var instance = await instanceService.CreateAsync(new Instance { ServerId = server.Id, Label = "main", ProxySlug = "main" });
        var model = await modelService.CreateAsync(new Model { ServerId = server.Id, Name = "qwen", Path = "/models/hf-cache/qwen" });
        var recipe = await recipeService.CreateAsync(new Recipe { ServerId = server.Id, Name = "default", ServedModelName = "main" });

        const string originalSnapshot = """["vllm","serve","/models/hf-cache/qwen","--served-model-name","main"]""";
        var created = await deploymentService.CreateAsync(new Deployment
        {
            InstanceId = instance.Id, ModelId = model.Id, RecipeId = recipe.Id,
            ArgsSnapshot = originalSnapshot, Status = "pending",
        });

        // jsonb round-trips through Postgres with its own canonical whitespace
        // (e.g. a space after each comma) -- compare parsed, not the raw string.
        static string Canonical(string json) => System.Text.Json.JsonSerializer.Serialize(System.Text.Json.JsonDocument.Parse(json).RootElement);

        var transitioned = await deploymentService.TransitionAsync(
            created.Id, status: "active", startedAt: DateTime.UtcNow);
        Assert.Equal(Canonical(originalSnapshot), Canonical(transitioned.ArgsSnapshot));

        transitioned = await deploymentService.TransitionAsync(
            created.Id, status: "stopped", stopReason: "manual", stoppedAt: DateTime.UtcNow);
        Assert.Equal(Canonical(originalSnapshot), Canonical(transitioned.ArgsSnapshot));
        Assert.Equal("stopped", transitioned.Status);
        Assert.Equal("manual", transitioned.StopReason);
    }

    [Fact]
    public async Task DeploymentService_CreateWithBlankArgsSnapshot_Rejected()
    {
        var scopeFactory = await NewMigratedScopeFactoryAsync();
        var server = await SeedServerAsync(scopeFactory, gpuName: "NVIDIA RTX 4090");
        var instanceService = new InstanceService(scopeFactory, new ReadOnlyGuard());
        var modelService = new ModelService(scopeFactory, new ReadOnlyGuard());
        var recipeService = new RecipeService(scopeFactory, new ReadOnlyGuard());
        var deploymentService = new DeploymentService(scopeFactory, new ReadOnlyGuard());

        var instance = await instanceService.CreateAsync(new Instance { ServerId = server.Id, Label = "main", ProxySlug = "main" });
        var model = await modelService.CreateAsync(new Model { ServerId = server.Id, Name = "qwen", Path = "/models/hf-cache/qwen" });
        var recipe = await recipeService.CreateAsync(new Recipe { ServerId = server.Id, Name = "default", ServedModelName = "main" });

        await Assert.ThrowsAsync<ArgumentException>(() => deploymentService.CreateAsync(new Deployment
        {
            InstanceId = instance.Id, ModelId = model.Id, RecipeId = recipe.Id,
            ArgsSnapshot = "", Status = "pending",
        }));
    }

    // --- friendly errors on constraint violations ---

    [Fact]
    public async Task DeploymentService_SecondConcurrentSwap_SurfacesAsFriendlyError()
    {
        var scopeFactory = await NewMigratedScopeFactoryAsync();
        var server = await SeedServerAsync(scopeFactory, gpuName: "NVIDIA RTX 4090");
        var instanceService = new InstanceService(scopeFactory, new ReadOnlyGuard());
        var modelService = new ModelService(scopeFactory, new ReadOnlyGuard());
        var recipeService = new RecipeService(scopeFactory, new ReadOnlyGuard());
        var deploymentService = new DeploymentService(scopeFactory, new ReadOnlyGuard());

        var instance = await instanceService.CreateAsync(new Instance { ServerId = server.Id, Label = "main", ProxySlug = "main" });
        var model = await modelService.CreateAsync(new Model { ServerId = server.Id, Name = "qwen", Path = "/models/hf-cache/qwen" });
        var recipe = await recipeService.CreateAsync(new Recipe { ServerId = server.Id, Name = "default", ServedModelName = "main" });

        // The index is narrowed to 'pending' only (P10a): a swap's new pending
        // row must coexist with the previous active one, so what's actually
        // "a second concurrent swap" is a second PENDING row, not pending
        // alongside an existing active one (which the test below exercises
        // separately as the real swap-in-progress case this error is for).
        await deploymentService.CreateAsync(new Deployment
        {
            InstanceId = instance.Id, ModelId = model.Id, RecipeId = recipe.Id,
            ArgsSnapshot = "[]", Status = "pending",
        });

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => deploymentService.CreateAsync(new Deployment
        {
            InstanceId = instance.Id, ModelId = model.Id, RecipeId = recipe.Id,
            ArgsSnapshot = "[]", Status = "pending",
        }));
        Assert.Contains("already in progress", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RecipeService_DuplicateNameOnSameServer_SurfacesAsFriendlyError()
    {
        var scopeFactory = await NewMigratedScopeFactoryAsync();
        var server = await SeedServerAsync(scopeFactory, gpuName: "NVIDIA RTX 4090");
        var service = new RecipeService(scopeFactory, new ReadOnlyGuard());

        await service.CreateAsync(new Recipe { ServerId = server.Id, Name = "default", ServedModelName = "main" });

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.CreateAsync(new Recipe { ServerId = server.Id, Name = "default", ServedModelName = "main" }));
        Assert.Contains("already exists", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task InstanceService_DuplicateProxySlug_SurfacesAsFriendlyError()
    {
        var scopeFactory = await NewMigratedScopeFactoryAsync();
        var server = await SeedServerAsync(scopeFactory);
        var service = new InstanceService(scopeFactory, new ReadOnlyGuard());

        await service.CreateAsync(new Instance { ServerId = server.Id, Label = "main", ProxySlug = "main" });

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.CreateAsync(new Instance { ServerId = server.Id, Label = "secondary", ProxySlug = "main" }));
        Assert.Contains("already in use", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    // --- recipe hard-block enforcement at save time ---

    [Fact]
    public async Task RecipeService_CreateWithBfloat16OnIntelServer_Rejected()
    {
        var scopeFactory = await NewMigratedScopeFactoryAsync();
        var server = await SeedServerAsync(scopeFactory, gpuName: "Intel Arc A770");
        var service = new RecipeService(scopeFactory, new ReadOnlyGuard());

        await Assert.ThrowsAsync<ArgumentException>(() => service.CreateAsync(
            new Recipe { ServerId = server.Id, Name = "bad", ServedModelName = "main", Dtype = "bfloat16" }));
    }

    [Fact]
    public async Task RecipeService_CreateWithFloat16OnIntelServer_Passes()
    {
        var scopeFactory = await NewMigratedScopeFactoryAsync();
        var server = await SeedServerAsync(scopeFactory, gpuName: "Intel Arc A770");
        var service = new RecipeService(scopeFactory, new ReadOnlyGuard());

        var created = await service.CreateAsync(
            new Recipe { ServerId = server.Id, Name = "good", ServedModelName = "main", Dtype = "float16" });
        Assert.True(created.Id > 0);
    }

    // --- duplicate ("name-copy") and create-from-model ---

    [Fact]
    public async Task RecipeService_Duplicate_CreatesANameCopyWithTheSameSettings()
    {
        var scopeFactory = await NewMigratedScopeFactoryAsync();
        var server = await SeedServerAsync(scopeFactory, gpuName: "NVIDIA RTX 4090");
        var service = new RecipeService(scopeFactory, new ReadOnlyGuard());

        var original = await service.CreateAsync(new Recipe
        {
            ServerId = server.Id, Name = "default", ServedModelName = "main", MaxModelLen = 32768,
        });

        var copy = await service.DuplicateAsync(original.Id);

        Assert.Equal("default-copy", copy.Name);
        Assert.Equal(32768, copy.MaxModelLen);
        Assert.NotEqual(original.Id, copy.Id);

        var secondCopy = await service.DuplicateAsync(original.Id);
        Assert.Equal("default-copy-2", secondCopy.Name);
    }

    [Fact]
    public async Task RecipeService_CreateFromModel_PrefillsNativeContextAndGpuMemoryUtilization()
    {
        var scopeFactory = await NewMigratedScopeFactoryAsync();
        var server = await SeedServerAsync(scopeFactory, gpuName: "NVIDIA RTX 4090");
        var modelService = new ModelService(scopeFactory, new ReadOnlyGuard());
        var recipeService = new RecipeService(scopeFactory, new ReadOnlyGuard());

        var model = await modelService.CreateAsync(new Model
        {
            ServerId = server.Id, Name = "qwen", Path = "/models/hf-cache/qwen",
            Config = """{"max_position_embeddings": 65536}""",
        });

        var recipe = await recipeService.CreateFromModelAsync(model.Id, "from-qwen");

        Assert.Equal(65536, recipe.MaxModelLen);
        Assert.Equal(0.90f, recipe.GpuMemoryUtilization);
    }

    [Fact]
    public async Task RecipeService_CreateFromModel_WithNoConfigOrMissingField_FallsBackToDefault()
    {
        // The Config column is jsonb -- Postgres itself rejects malformed JSON
        // before it would ever reach ReadNativeContextLength's own JsonException
        // guard, so the realistic "can't determine native context" shapes at
        // this layer are: no config at all, or valid JSON missing the field
        // (P08's "downloaded but config unreadable" is a check against the RAW
        // HF repo file, a different layer entirely, before anything is stored).
        var scopeFactory = await NewMigratedScopeFactoryAsync();
        var server = await SeedServerAsync(scopeFactory, gpuName: "NVIDIA RTX 4090");
        var modelService = new ModelService(scopeFactory, new ReadOnlyGuard());
        var recipeService = new RecipeService(scopeFactory, new ReadOnlyGuard());

        var model = await modelService.CreateAsync(new Model
        {
            ServerId = server.Id, Name = "qwen", Path = "/models/hf-cache/qwen", Config = """{"some_other_field": 1}""",
        });

        var recipe = await recipeService.CreateFromModelAsync(model.Id, "from-qwen");

        Assert.Equal(new Dashboard.Core.Recipes.Recipe().MaxModelLen, recipe.MaxModelLen);
    }

    // --- P13's "New recipe" entry point: an unsaved draft + live validation ---

    [Fact]
    public async Task RecipeService_NewDraftForModel_PrefillsFromModelConfigAndGivenServedName()
    {
        var scopeFactory = await NewMigratedScopeFactoryAsync();
        var server = await SeedServerAsync(scopeFactory, gpuName: "NVIDIA RTX 4090");
        var modelService = new ModelService(scopeFactory, new ReadOnlyGuard());
        var recipeService = new RecipeService(scopeFactory, new ReadOnlyGuard());

        var model = await modelService.CreateAsync(new Model
        {
            ServerId = server.Id, Name = "qwen", Path = "/models/hf-cache/qwen",
            Config = """{"max_position_embeddings": 32768}""",
        });

        var draft = recipeService.NewDraftForModel(model, "custom-served-name");

        Assert.Equal(0, draft.Id); // unsaved
        Assert.Equal(server.Id, draft.ServerId);
        Assert.Equal("custom-served-name", draft.ServedModelName);
        Assert.Equal(32768, draft.MaxModelLen);
        Assert.Equal(0.90f, draft.GpuMemoryUtilization);
    }

    [Fact]
    public async Task RecipeService_ValidateDraftAsync_WithNoModelId_SkipsModelDependentRules()
    {
        var scopeFactory = await NewMigratedScopeFactoryAsync();
        var server = await SeedServerAsync(scopeFactory, gpuName: "Intel (12 GB)");
        var recipeService = new RecipeService(scopeFactory, new ReadOnlyGuard());

        var draft = new Recipe { ServerId = server.Id, Name = "draft", ServedModelName = "main", Dtype = "bfloat16" };

        var result = await recipeService.ValidateDraftAsync(draft, modelId: null);

        // The Intel dtype=bfloat16 rule is model-independent -- still fires.
        Assert.Contains(result.HardBlocks, i => i.Code == "dtype_bfloat16_blocked_on_intel");
        // But nothing model-dependent (e.g. path-under-hf-cache) can fire with no model.
        Assert.DoesNotContain(result.HardBlocks, i => i.Code == "model_path_outside_hf_cache");
    }

    [Fact]
    public async Task RecipeService_ValidateDraftAsync_OnAnUnsavedDraft_NeverThrowsEvenThoughItHasNoId()
    {
        var scopeFactory = await NewMigratedScopeFactoryAsync();
        var server = await SeedServerAsync(scopeFactory, gpuName: "NVIDIA RTX 4090");
        var recipeService = new RecipeService(scopeFactory, new ReadOnlyGuard());

        var draft = new Recipe { Id = 0, ServerId = server.Id, Name = "draft", GpuMemoryUtilization = 5f };

        var result = await recipeService.ValidateDraftAsync(draft, modelId: null);

        Assert.Contains(result.HardBlocks, i => i.Message.Contains("gpu_memory_utilization"));
    }

    // --- validating an existing legacy/converted-shaped row ---

    [Fact]
    public async Task RecipeService_ValidateAsync_OnALegacyShapedRow_ReturnsIssuesWithoutThrowing()
    {
        // The seed fixture's own model path ("/models/qwen3.8-int4") is NOT under
        // this server's models_dir/hf-cache/ -- exactly the shape a P01b-converted
        // row can have (doc 13 gap 12). Validate() must still just report it.
        var scopeFactory = await NewMigratedScopeFactoryAsync();
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var server = await V2SeedFixture.SeedAsync(db);
        var model = await db.ModelsV2.FirstAsync(m => m.ServerId == server.Id);
        var recipe = await db.RecipesV2.FirstAsync(r => r.ServerId == server.Id);

        var service = new RecipeService(scopeFactory, new ReadOnlyGuard());

        var result = await service.ValidateAsync(recipe.Id, model.Id);

        Assert.Contains(result.HardBlocks, i => i.Code == "model_path_outside_hf_cache");
    }
}
