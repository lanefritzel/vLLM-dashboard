using Dashboard.Core.Models;

namespace Dashboard.Core.Recipes;

/// <summary>Engine-neutral entry point over the per-engine static builders, for
/// callers that only know a <see cref="SourceType"/> (mirrors IMetricsSource).</summary>
public interface IArgBuilder
{
    SourceType Engine { get; }

    /// <summary>Throws <see cref="ArgumentException"/> if <paramref name="recipe"/> is
    /// not this engine's recipe type.</summary>
    ArgBuilderResult Build(RecipeModel model, Recipe recipe, RecipeInstance instance, RecipeServer server);
}

public interface IArgBuilderFactory
{
    /// <summary>Throws <see cref="NotSupportedException"/> for an engine the dashboard
    /// doesn't build argv for (e.g. Ollama, Mock).</summary>
    IArgBuilder Create(SourceType engine);
}

public sealed class ArgBuilderFactory : IArgBuilderFactory
{
    public IArgBuilder Create(SourceType engine) => engine switch
    {
        SourceType.Vllm => new VllmEngineBuilder(),
        SourceType.LlamaCpp => new LlamaCppEngineBuilder(),
        _ => throw new NotSupportedException($"No arg builder for engine '{engine}'."),
    };

    private sealed class VllmEngineBuilder : IArgBuilder
    {
        public SourceType Engine => SourceType.Vllm;

        public ArgBuilderResult Build(RecipeModel model, Recipe recipe, RecipeInstance instance, RecipeServer server) =>
            recipe is VllmRecipe vllm
                ? ArgBuilder.Build(model, vllm, instance, server)
                : throw new ArgumentException($"A vLLM deployment needs a {nameof(VllmRecipe)}, got {recipe.GetType().Name}.", nameof(recipe));
    }

    private sealed class LlamaCppEngineBuilder : IArgBuilder
    {
        public SourceType Engine => SourceType.LlamaCpp;

        public ArgBuilderResult Build(RecipeModel model, Recipe recipe, RecipeInstance instance, RecipeServer server) =>
            recipe is LlamaCppRecipe llama
                ? LlamaCppArgBuilder.Build(model, llama, instance, server)
                : throw new ArgumentException($"A llama.cpp deployment needs a {nameof(LlamaCppRecipe)}, got {recipe.GetType().Name}.", nameof(recipe));
    }
}
