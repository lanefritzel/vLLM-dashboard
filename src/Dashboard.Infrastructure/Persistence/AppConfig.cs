namespace Dashboard.Infrastructure.Persistence;

public class AppConfig
{
    public string Key { get; set; } = "";
    public string Value { get; set; } = "";
    public DateTime UpdatedAt { get; set; }
    public string? UpdatedBy { get; set; }
}
