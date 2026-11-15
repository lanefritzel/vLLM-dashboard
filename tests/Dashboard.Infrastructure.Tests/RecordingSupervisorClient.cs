namespace Dashboard.Infrastructure.Tests;

/// <summary>Records every <see cref="DeleteModelFilesAsync"/> call (admin url + the
/// exact path requested) and otherwise behaves like <see cref="UnusedSupervisorClient"/> --
/// for tests asserting ModelService resolved the RIGHT path to hand the supervisor
/// (e.g. the containing folder, not just the single file).</summary>
public sealed class RecordingSupervisorClient : UnusedSupervisorClient
{
    public List<(string AdminUrl, string Path)> DeleteCalls { get; } = new();

    public override Task DeleteModelFilesAsync(string adminUrl, string path, CancellationToken ct = default)
    {
        DeleteCalls.Add((adminUrl, path));
        return Task.CompletedTask;
    }
}
