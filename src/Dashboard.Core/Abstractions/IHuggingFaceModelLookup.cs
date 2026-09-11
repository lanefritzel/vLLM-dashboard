using Dashboard.Core.Models;

namespace Dashboard.Core.Abstractions;

/// <summary>Looks up public metadata for a Hugging Face repo. Best-effort: returns
/// null only when the repo itself couldn't be found/reached at all, otherwise
/// returns whatever fields it could determine.</summary>
public interface IHuggingFaceModelLookup
{
    Task<HuggingFaceModelInfo?> LookupAsync(string repoId, CancellationToken ct = default);
}
