using System.Globalization;

namespace Dashboard.Core.Recipes;

/// <summary>
/// llama.cpp's own <c>llama-server</c> argv, built from a <see cref="LlamaCppRecipe"/>
/// (P1, promoted from the multi-engine spike). Flag names were confirmed against a
/// real llama-server run. A <see cref="LlamaCppRecipe"/> has no vLLM dials, so none
/// can leak into this argv.
/// </summary>
public static class LlamaCppArgBuilder
{
    public static ArgBuilderResult Build(RecipeModel model, LlamaCppRecipe recipe, RecipeInstance instance, RecipeServer server)
    {
        var argv = new List<string>
        {
            "llama-server",
            "--model", ArgBuilderCommon.ContainerModelPath(model.Path, server.ModelsDir),
        };

        ArgBuilderCommon.AppendFlag(argv, EngineFlags.LlamaCppAlias, recipe.ServedModelName);
        ArgBuilderCommon.AppendFlag(argv, EngineFlags.LlamaCppCtxSize, recipe.MaxModelLen.ToString(CultureInfo.InvariantCulture));

        // /metrics is only served with --metrics; the dashboard's scraper needs it.
        ArgBuilderCommon.AppendFlag(argv, "--metrics", null);

        // extra_args: may override anything above, same semantics as ArgBuilder.
        foreach (var extra in recipe.ExtraArgs)
            ArgBuilderCommon.AppendFlag(argv, "--" + extra.Key, string.IsNullOrEmpty(extra.Value) ? null : extra.Value);

        // The instance's own port, injected last.
        ArgBuilderCommon.AppendFlag(argv, "--port", instance.Port.ToString(CultureInfo.InvariantCulture));

        return new ArgBuilderResult { Argv = argv, Env = ArgBuilderCommon.BuildEnv(recipe) };
    }
}
