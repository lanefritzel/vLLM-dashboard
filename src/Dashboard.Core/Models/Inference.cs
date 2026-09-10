namespace Dashboard.Core.Models;

public sealed record InferenceRequest
{
    public string Prompt { get; init; } = "";
    public int MaxTokens { get; init; } = 256;
    public string? Model { get; init; }
}

public sealed record InferenceResult
{
    public bool Success { get; init; }
    public string? Text { get; init; }
    public int? LatencyMs { get; init; }
    public int? TokensUsed { get; init; }
    public string? Error { get; init; }
}
