using System.Globalization;

namespace Dashboard.Core.Recipes;

/// <summary>
/// Spike (multi-engine support proposal): llama.cpp's own <c>llama-server</c> argv,
/// built from the SAME <see cref="Recipe"/>/<see cref="RecipeModel"/> shape
/// <see cref="ArgBuilder"/> (vLLM) uses. Deliberately uses only the handful of
/// fields that mean the same thing on both engines (served name, context length,
/// port, extra_args/extra_env) -- every vLLM-only dial (GPU memory fraction,
/// kv-cache dtype, block size, speculative decoding, tool/reasoning parsers) is
/// read by <see cref="ArgBuilder"/> but ignored here, on purpose, since llama.cpp
/// has no equivalent. That gap is the answer the spike was for: Recipe will need
/// to split per engine before this is more than a prototype.
/// </summary>
public static class LlamaCppArgBuilder
{
    public static ArgBuilderResult Build(RecipeModel model, Recipe recipe, RecipeInstance instance, RecipeServer server)
    {
        var argv = new List<string>
        {
            "llama-server",
            "--model", ArgBuilder.ContainerModelPath(model.Path, server.ModelsDir),
        };

        ArgBuilder.AppendFlag(argv, "--alias", recipe.ServedModelName);
        ArgBuilder.AppendFlag(argv, "--ctx-size", recipe.MaxModelLen.ToString(CultureInfo.InvariantCulture));

        // extra_args: may override anything above, same semantics as ArgBuilder.
        foreach (var extra in recipe.ExtraArgs)
            ArgBuilder.AppendFlag(argv, "--" + extra.Key, string.IsNullOrEmpty(extra.Value) ? null : extra.Value);

        // The instance's own port, injected last.
        ArgBuilder.AppendFlag(argv, "--port", instance.Port.ToString(CultureInfo.InvariantCulture));

        var env = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var e in recipe.ExtraEnv)
            env[e.Key] = e.Value; // later entries win on a duplicate key

        return new ArgBuilderResult { Argv = argv, Env = env };
    }
}
