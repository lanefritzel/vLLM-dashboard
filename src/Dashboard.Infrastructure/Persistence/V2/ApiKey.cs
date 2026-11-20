namespace Dashboard.Infrastructure.Persistence.V2;

/// <summary>A credential for the machine-to-machine inference proxy (table <c>api_keys</c>).
/// Only <see cref="KeyHash"/> is ever persisted -- the raw key is returned once, at
/// generation time, and never stored. <see cref="UserId"/> is nullable so a single-user
/// deployment can run with one owner key that isn't tied to any particular account;
/// multi-user support later is just populating it on new keys, not a schema change.</summary>
public class ApiKey
{
    public int Id { get; set; }
    public string KeyHash { get; set; } = "";
    public string Label { get; set; } = "";
    public string? UserId { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? LastUsedAt { get; set; }
    public DateTime? RevokedAt { get; set; }
}
