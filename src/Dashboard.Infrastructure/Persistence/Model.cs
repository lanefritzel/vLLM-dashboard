using Dashboard.Core.Models;

namespace Dashboard.Infrastructure.Persistence;

public class Model
{
    public Guid Id { get; set; }
    public Guid SourceId { get; set; }
    public string Name { get; set; } = "";
    public string? DisplayName { get; set; }

    /// <summary>Deprecated: superseded by ModelSource + ModelPathOrRepoId. Column kept
    /// (unused going forward) rather than a destructive drop; existing rows are
    /// backfilled into ModelPathOrRepoId by the migration that introduced these fields.</summary>
    public string? SwapArgs { get; set; }

    public string ModelSource { get; set; } = "huggingface";
    public string ModelPathOrRepoId { get; set; } = "";
    public string? ImageOverride { get; set; }
    public string OptionsJson { get; set; } = "{}";

    public bool IsActive { get; set; }
    public int SortOrder { get; set; }
    public int? IdleTtlSeconds { get; set; }
    public bool PreloadOnStartup { get; set; }

    public Source? Source { get; set; }

    public Core.Models.ModelInfo ToInfo()
    {
        var info = new Core.Models.ModelInfo
        {
            Id = Id.ToString(),
            SourceId = SourceId.ToString(),
            Name = Name,
            DisplayName = DisplayName,
            ModelSource = ModelSourceExtensions.FromStorage(ModelSource),
            ModelPathOrRepoId = ModelPathOrRepoId,
            ImageOverride = ImageOverride,
            IsActive = IsActive,
            SortOrder = SortOrder,
            IdleTtlSeconds = IdleTtlSeconds,
            PreloadOnStartup = PreloadOnStartup,
        };

        try
        {
            info.Options = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(OptionsJson) ?? new();
        }
        catch
        {
            // Malformed JSON: leave default (empty).
        }

        return info;
    }
}
