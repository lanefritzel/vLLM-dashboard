using Dashboard.Core.Abstractions;
using Dashboard.Core.Models;
using Dashboard.Core.Recipes;
using Dashboard.Infrastructure.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Dashboard.Infrastructure.Persistence.V2;

public interface IRecipeService
{
    Task<IReadOnlyList<Recipe>> ListByServerAsync(int serverId, CancellationToken ct = default);
    Task<Recipe?> GetAsync(int id, CancellationToken ct = default);

    /// <summary>Hard-blocks (throws) on a blank name or any <see cref="CompatibilityPass.CheckRecipe"/>
    /// hard issue (dtype/field rules) -- a recipe is model-agnostic, so only the
    /// model-independent rules apply here; the model-dependent ones (path, sizing)
    /// only make sense once a model is actually picked, at deploy pre-flight.</summary>
    Task<Recipe> CreateAsync(Recipe recipe, CancellationToken ct = default);

    Task<Recipe> UpdateAsync(Recipe recipe, CancellationToken ct = default);
    Task DeleteAsync(int id, CancellationToken ct = default);

    /// <summary>"name-copy" (doc 8 §4): a full copy of an existing recipe under a
    /// new, unique name on the same server.</summary>
    Task<Recipe> DuplicateAsync(int recipeId, CancellationToken ct = default);

    /// <summary>"Create recipe from this model": a fresh recipe pre-filled with
    /// max_model_len = the model's own native context length (falls back to the
    /// schema default when the model's config doesn't have one) and
    /// gpu_memory_utilization = 0.90 (doc 8 §4).</summary>
    Task<Recipe> CreateFromModelAsync(int modelId, string name, CancellationToken ct = default);

    /// <summary>Non-throwing -- the same <c>Validate(model, recipe, server) -&gt; issues[]</c>
    /// the page asks for, shared by save (which enforces the model-independent
    /// subset by throwing, see <see cref="CreateAsync"/>), deploy pre-flight, and
    /// the UI. Must be callable on an existing, possibly legacy-shaped row without
    /// throwing (doc 13 gap 12) -- it just reports issues.</summary>
    Task<CompatibilityResult> ValidateAsync(int recipeId, int modelId, SourceType? engine = null, CancellationToken ct = default);

    /// <summary>Same non-throwing validation as <see cref="ValidateAsync"/>, but for
    /// a draft that may not be saved yet (P13's recipe editor: inline validation as
    /// the operator types, before a Save -- `recipeId` doesn't exist yet so
    /// <see cref="ValidateAsync"/> can't be used). `modelId` is optional -- the
    /// model-dependent rules are skipped when it's null (no model picked yet), same
    /// as a recipe being model-agnostic at save time.</summary>
    Task<CompatibilityResult> ValidateDraftAsync(Recipe draft, int? modelId, SourceType? engine = null, CancellationToken ct = default);

    /// <summary>Builds an UNSAVED draft recipe for the editor's "New recipe" entry
    /// point (P13) -- max_model_len prefilled from the model's own native context
    /// length (same source <see cref="CreateFromModelAsync"/> uses), gpu_memory_utilization
    /// 0.90. Pure/no I/O; the caller supplies <paramref name="servedModelName"/> since
    /// resolving "the instance's currently-active served name, or main" needs
    /// instance/deployment context this service doesn't otherwise depend on.</summary>
    Recipe NewDraftForModel(Model model, string servedModelName);
}

/// <summary>CRUD for the new-schema <see cref="Recipe"/> table (P09b).</summary>
public sealed class RecipeService : IRecipeService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IReadOnlyGuard _readOnlyGuard;

    public RecipeService(IServiceScopeFactory scopeFactory, IReadOnlyGuard readOnlyGuard)
    {
        _scopeFactory = scopeFactory;
        _readOnlyGuard = readOnlyGuard;
    }

    private static void ValidateNameForSave(Recipe recipe)
    {
        if (string.IsNullOrWhiteSpace(recipe.Name))
            throw new ArgumentException("A recipe must have a name.");
    }

    /// <summary>Throws with every hard issue's message joined together when the
    /// model-independent rules (dtype/field rules) are violated -- this is the
    /// "hard-blocked" enforcement the DoD's incident-reproduction unit tests
    /// exercise at save time. <see cref="IRecipeService.ValidateAsync"/> is the
    /// non-throwing equivalent, for callers (deploy pre-flight, the UI, or
    /// re-checking an existing legacy row) that need issues without an
    /// exception.</summary>
    private static void ValidateFieldRulesForSave(Recipe recipe, Server server, SourceType engine)
    {
        var result = CompatibilityPass.CheckRecipe(CoreRecipeMappers.ToCoreRecipe(recipe, engine), CoreRecipeMappers.ToCoreServer(server));
        if (result.HardBlocks.Count > 0)
            throw new ArgumentException(string.Join(" ", result.HardBlocks.Select(i => i.Message)));
    }

    public async Task<IReadOnlyList<Recipe>> ListByServerAsync(int serverId, CancellationToken ct = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.RecipesV2.Where(r => r.ServerId == serverId).OrderBy(r => r.Name).ToListAsync(ct);
    }

    public async Task<Recipe?> GetAsync(int id, CancellationToken ct = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.RecipesV2.FindAsync([id], ct);
    }

    public async Task<Recipe> CreateAsync(Recipe recipe, CancellationToken ct = default)
    {
        _readOnlyGuard.EnsureWritable();
        ValidateNameForSave(recipe);
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var server = await RequireServerAsync(db, recipe.ServerId, ct);
        ValidateFieldRulesForSave(recipe, server, await ResolveEngineAsync(db, recipe.ServerId, ct));

        db.RecipesV2.Add(recipe);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (PostgresErrors.IsUniqueViolation(ex))
        {
            throw new InvalidOperationException($"A recipe named '{recipe.Name}' already exists on this server.", ex);
        }
        return recipe;
    }

    public async Task<Recipe> UpdateAsync(Recipe recipe, CancellationToken ct = default)
    {
        _readOnlyGuard.EnsureWritable();
        ValidateNameForSave(recipe);
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var server = await RequireServerAsync(db, recipe.ServerId, ct);
        ValidateFieldRulesForSave(recipe, server, await ResolveEngineAsync(db, recipe.ServerId, ct));

        db.RecipesV2.Update(recipe);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (PostgresErrors.IsUniqueViolation(ex))
        {
            throw new InvalidOperationException($"A recipe named '{recipe.Name}' already exists on this server.", ex);
        }
        return recipe;
    }

    public async Task DeleteAsync(int id, CancellationToken ct = default)
    {
        _readOnlyGuard.EnsureWritable();
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var recipe = await db.RecipesV2.FindAsync([id], ct);
        if (recipe is null)
            return;

        // Deployment history keeps pointing at the recipe it was built from (FK, restrict),
        // so a recipe that has ever been deployed can't be removed without erasing that history.
        var used = await db.DeploymentsV2.CountAsync(d => d.RecipeId == id, ct);
        if (used > 0)
            throw new InvalidOperationException(
                $"Recipe '{recipe.Name}' can't be deleted: it was used by {used} deployment{(used == 1 ? "" : "s")} " +
                "that are kept in the Deployments history. Edit it instead, or create a new recipe and leave this one.");

        db.RecipesV2.Remove(recipe);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (PostgresErrors.IsForeignKeyViolation(ex))
        {
            throw new InvalidOperationException($"Recipe '{recipe.Name}' can't be deleted: something else still refers to it.", ex);
        }
    }

    public async Task<Recipe> DuplicateAsync(int recipeId, CancellationToken ct = default)
    {
        _readOnlyGuard.EnsureWritable();
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var source = await db.RecipesV2.FindAsync([recipeId], ct)
            ?? throw new KeyNotFoundException($"No recipe with id {recipeId}.");

        var copy = CloneWithoutIdentity(source);
        copy.Name = await NextAvailableCopyNameAsync(db, source.ServerId, source.Name, ct);

        db.RecipesV2.Add(copy);
        await db.SaveChangesAsync(ct);
        return copy;
    }

    public async Task<Recipe> CreateFromModelAsync(int modelId, string name, CancellationToken ct = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var model = await db.ModelsV2.FindAsync([modelId], ct)
            ?? throw new KeyNotFoundException($"No model with id {modelId}.");

        var recipe = new Recipe
        {
            ServerId = model.ServerId,
            Name = name,
            MaxModelLen = CoreRecipeMappers.ReadNativeContextLength(model.Config) ?? new Recipe().MaxModelLen,
            GpuMemoryUtilization = 0.90f,
        };
        return await CreateAsync(recipe, ct);
    }

    public async Task<CompatibilityResult> ValidateAsync(int recipeId, int modelId, SourceType? engine = null, CancellationToken ct = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var recipe = await db.RecipesV2.FindAsync([recipeId], ct)
            ?? throw new KeyNotFoundException($"No recipe with id {recipeId}.");
        var model = await db.ModelsV2.FindAsync([modelId], ct)
            ?? throw new KeyNotFoundException($"No model with id {modelId}.");
        var server = await RequireServerAsync(db, recipe.ServerId, ct);

        var effective = engine ?? await ResolveEngineAsync(db, recipe.ServerId, ct);
        return CompatibilityPass.Check(
            CoreRecipeMappers.ToCoreModel(model), CoreRecipeMappers.ToCoreRecipe(recipe, effective), CoreRecipeMappers.ToCoreServer(server));
    }

    public async Task<CompatibilityResult> ValidateDraftAsync(Recipe draft, int? modelId, SourceType? engine = null, CancellationToken ct = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var server = await RequireServerAsync(db, draft.ServerId, ct);
        var effective = engine ?? await ResolveEngineAsync(db, draft.ServerId, ct);

        if (modelId is null)
            return CompatibilityPass.CheckRecipe(CoreRecipeMappers.ToCoreRecipe(draft, effective), CoreRecipeMappers.ToCoreServer(server));

        var model = await db.ModelsV2.FindAsync([modelId.Value], ct)
            ?? throw new KeyNotFoundException($"No model with id {modelId}.");
        return CompatibilityPass.Check(
            CoreRecipeMappers.ToCoreModel(model), CoreRecipeMappers.ToCoreRecipe(draft, effective), CoreRecipeMappers.ToCoreServer(server));
    }

    public Recipe NewDraftForModel(Model model, string servedModelName) => new()
    {
        ServerId = model.ServerId,
        ServedModelName = servedModelName,
        MaxModelLen = CoreRecipeMappers.ReadNativeContextLength(model.Config) ?? new Recipe().MaxModelLen,
        GpuMemoryUtilization = 0.90f,
    };

    /// <summary>Which engine's rules apply to a recipe when the caller doesn't say. Recipes belong to
    /// a server, not an instance, so this looks at the server's instances: llama.cpp only when every
    /// instance there is llama.cpp. A server with no instances, any vLLM instance, or a mix gets the
    /// vLLM rules (the stricter set -- the recipe could be deployed to a vLLM instance); a swap
    /// re-checks against its own instance's engine regardless.</summary>
    private static async Task<SourceType> ResolveEngineAsync(AppDbContext db, int serverId, CancellationToken ct)
    {
        var engines = await db.InstancesV2.Where(i => i.ServerId == serverId).Select(i => i.Engine).Distinct().ToListAsync(ct);
        return engines.Count > 0 && engines.All(e => e == SourceType.LlamaCpp.ToStorage()) ? SourceType.LlamaCpp : SourceType.Vllm;
    }

    private static async Task<Server> RequireServerAsync(AppDbContext db, int serverId, CancellationToken ct) =>
        await db.ServersV2.FindAsync([serverId], ct)
            ?? throw new KeyNotFoundException($"No server with id {serverId}.");

    private static async Task<string> NextAvailableCopyNameAsync(AppDbContext db, int serverId, string baseName, CancellationToken ct)
    {
        var existingNames = await db.RecipesV2.Where(r => r.ServerId == serverId).Select(r => r.Name).ToListAsync(ct);
        var candidate = $"{baseName}-copy";
        var suffix = 2;
        while (existingNames.Contains(candidate))
        {
            candidate = $"{baseName}-copy-{suffix}";
            suffix++;
        }
        return candidate;
    }

    private static Recipe CloneWithoutIdentity(Recipe source) => new()
    {
        ServerId = source.ServerId,
        Name = source.Name,
        Description = source.Description,
        ServedModelName = source.ServedModelName,
        MaxModelLen = source.MaxModelLen,
        GpuMemoryUtilization = source.GpuMemoryUtilization,
        KvCacheDtype = source.KvCacheDtype,
        Dtype = source.Dtype,
        EnforceEager = source.EnforceEager,
        MaxNumBatchedTokens = source.MaxNumBatchedTokens,
        BlockSize = source.BlockSize,
        SpeculativeDecoding = source.SpeculativeDecoding,
        SpeculativeTokens = source.SpeculativeTokens,
        EnableAutoToolChoice = source.EnableAutoToolChoice,
        ToolCallParser = source.ToolCallParser,
        ReasoningParser = source.ReasoningParser,
        ExtraArgs = source.ExtraArgs,
        ExtraEnv = source.ExtraEnv,
    };

}
