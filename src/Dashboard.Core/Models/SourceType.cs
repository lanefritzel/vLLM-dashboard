namespace Dashboard.Core.Models;

public enum SourceType
{
    Vllm,
    Ollama,
    Mock,
}

public static class SourceTypeExtensions
{
    public static string ToStorage(this SourceType type) => type switch
    {
        SourceType.Vllm => "vllm",
        SourceType.Ollama => "ollama",
        SourceType.Mock => "mock",
        _ => "vllm",
    };

    public static SourceType FromStorage(string? value)
    {
        var v = value?.Trim().ToLowerInvariant();
        return v switch
        {
            "ollama" => SourceType.Ollama,
            "mock" => SourceType.Mock,
            _ => SourceType.Vllm,
        };
    }
}
