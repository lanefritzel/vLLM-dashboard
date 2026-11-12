namespace Dashboard.Core.Recipes;

/// <summary>The argv flags that differ between engines for the shared recipe fields. The arg builders
/// emit them and the recipe editor labels its fields with them, so the two can't drift apart.</summary>
public static class EngineFlags
{
    public const string VllmServedModelName = "--served-model-name";
    public const string VllmMaxModelLen = "--max-model-len";
    public const string LlamaCppAlias = "--alias";
    public const string LlamaCppCtxSize = "--ctx-size";
    public const string LlamaCppGpuLayers = "--gpu-layers";
}
