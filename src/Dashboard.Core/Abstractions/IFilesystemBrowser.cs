namespace Dashboard.Core.Abstractions;

/// <summary>Read-only directory browsing confined to a single mounted root
/// (MODELS_BROWSE_ROOT, default "/models") -- lets the UI offer a navigable
/// folder picker for Server.ModelsDir / Model.Path instead of a free-text
/// field a user can fat-finger. Lists subfolders (always) plus standalone
/// model files (.gguf only -- never .safetensors/.bin shards, which are only
/// ever valid as part of their containing folder, for either engine). Never
/// resolves outside its root.</summary>
public interface IFilesystemBrowser
{
    /// <summary>False when the configured root isn't mounted into this
    /// container at all (e.g. local dev without the bind mount) -- callers
    /// should fall back to a plain text field rather than show an empty browser.</summary>
    bool IsAvailable { get; }

    string RootPath { get; }

    /// <summary>Lists the immediate subfolders and standalone model files of
    /// <paramref name="path"/>. A null/blank/out-of-root path is clamped back to
    /// <see cref="RootPath"/> rather than throwing -- this is a browse-forward UI,
    /// not an API that needs to reject bad input.</summary>
    FolderBrowseResult ListFolders(string? path);
}

public sealed record FolderEntry(string Name, string FullPath);

public sealed record FolderBrowseResult
{
    public required string CurrentPath { get; init; }
    public required bool CanGoUp { get; init; }
    public string? ParentPath { get; init; }
    public required IReadOnlyList<FolderEntry> Subfolders { get; init; }

    /// <summary>Standalone single-file models in this folder (.gguf only). A caller
    /// that only ever wants a folder (e.g. Server.ModelsDir) can simply ignore this --
    /// it's additive, not a behavior change for existing callers.</summary>
    public required IReadOnlyList<FolderEntry> Files { get; init; }
}
