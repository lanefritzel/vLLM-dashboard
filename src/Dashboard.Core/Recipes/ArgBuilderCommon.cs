namespace Dashboard.Core.Recipes;

/// <summary>Argv/env helpers shared by every engine's builder (P1).</summary>
internal static class ArgBuilderCommon
{
    /// <summary>Appends "flag [value]" to argv, or — if that flag is already present
    /// — replaces its value in place (or removes a stale value if it's now a bare
    /// toggle), so a later caller (extra_args overriding a dial) never produces the
    /// same flag twice.</summary>
    internal static void AppendFlag(List<string> argv, string flag, string? value)
    {
        var existingIndex = argv.IndexOf(flag);
        if (existingIndex >= 0)
        {
            var hadValue = existingIndex + 1 < argv.Count && !argv[existingIndex + 1].StartsWith("--", StringComparison.Ordinal);
            if (hadValue)
                argv.RemoveAt(existingIndex + 1);
            if (value is not null)
                argv.Insert(existingIndex + 1, value);
            return;
        }

        argv.Add(flag);
        if (value is not null)
            argv.Add(value);
    }

    /// <summary>Host models.path with the server's models_dir prefix replaced by
    /// /models (e.g. &lt;models_dir&gt;/my-model -> /models/my-model) -- works for
    /// any path under models_dir, whatever structure it's actually in.
    /// Leaves the path untouched if it doesn't actually start with that prefix --
    /// a mismatch here is a data problem for the compatibility pass to flag
    /// (and, pre-conversion, for P01b's report), not something to silently guess at.</summary>
    internal static string ContainerModelPath(string hostPath, string modelsDir)
    {
        var root = modelsDir.TrimEnd('/');
        return hostPath.StartsWith(root, StringComparison.Ordinal)
            ? "/models" + hostPath[root.Length..]
            : hostPath;
    }

    /// <summary>extra_env -> env dict; later entries win on a duplicate key.</summary>
    internal static Dictionary<string, string> BuildEnv(Recipe recipe)
    {
        var env = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var e in recipe.ExtraEnv)
            env[e.Key] = e.Value;
        return env;
    }
}
