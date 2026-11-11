namespace Dashboard.Core.Models;

public enum SourceType
{
    Vllm,
    Ollama,
    Mock,
    LlamaCpp,
}

public static class SourceTypeExtensions
{
    public static string ToStorage(this SourceType type) => type switch
    {
        SourceType.Vllm => "vllm",
        SourceType.Ollama => "ollama",
        SourceType.Mock => "mock",
        SourceType.LlamaCpp => "llamacpp",
        _ => "vllm",
    };

    public static SourceType FromStorage(string? value)
    {
        var v = value?.Trim().ToLowerInvariant();
        return v switch
        {
            "ollama" => SourceType.Ollama,
            "mock" => SourceType.Mock,
            "llamacpp" => SourceType.LlamaCpp,
            _ => SourceType.Vllm,
        };
    }
}
