namespace Dashboard.Infrastructure.Conversion;

/// <summary>One line of the conversion report -- a dropped row, a flagged value, a
/// path mismatch, anything the owner should read before trusting the result.</summary>
public sealed record ConversionReportLine(string Code, string Message);

/// <summary>What <see cref="ILegacyConversionService"/> did on one run, per P01b's
/// "emit a conversion report" work item. Counts are of rows actually created this
/// run -- a no-op (idempotent) re-run reports zeros everywhere.</summary>
public sealed record ConversionReport
{
    public int ServersConverted { get; init; }
    public int InstancesConverted { get; init; }
    public int ModelsConverted { get; init; }
    public int RecipesCreated { get; init; }
    public IReadOnlyList<string> OllamaSourcesDropped { get; init; } = Array.Empty<string>();
    public IReadOnlyList<ConversionReportLine> Notes { get; init; } = Array.Empty<ConversionReportLine>();

    public string ToText()
    {
        var lines = new List<string>
        {
            $"Servers converted: {ServersConverted}",
            $"Instances converted: {InstancesConverted}",
            $"Models converted: {ModelsConverted}",
            $"Recipes created: {RecipesCreated}",
            $"Ollama sources dropped: {OllamaSourcesDropped.Count}",
        };
        lines.AddRange(OllamaSourcesDropped.Select(s => $"  - {s}"));
        lines.Add($"Notes: {Notes.Count}");
        lines.AddRange(Notes.Select(n => $"  [{n.Code}] {n.Message}"));
        return string.Join(Environment.NewLine, lines);
    }
}
