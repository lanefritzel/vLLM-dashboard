namespace Dashboard.Infrastructure.Persistence;

public class Model
{
    public Guid Id { get; set; }
    public Guid SourceId { get; set; }
    public string Name { get; set; } = "";
    public string? DisplayName { get; set; }
    public string? SwapArgs { get; set; }
    public bool IsActive { get; set; }
    public int SortOrder { get; set; }

    public Source? Source { get; set; }

    public Core.Models.ModelInfo ToInfo() => new()
    {
        Id = Id.ToString(),
        SourceId = SourceId.ToString(),
        Name = Name,
        DisplayName = DisplayName,
        SwapArgs = SwapArgs,
        IsActive = IsActive,
        SortOrder = SortOrder,
    };
}
