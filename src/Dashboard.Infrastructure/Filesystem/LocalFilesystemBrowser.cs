using Dashboard.Core.Abstractions;
using Microsoft.Extensions.Configuration;

namespace Dashboard.Infrastructure.Filesystem;

/// <summary>Browses whatever is bind-mounted into this container at
/// MODELS_BROWSE_ROOT (default "/models") -- the same host directory the
/// supervisor mounts as its own MODELS_ROOT (see deploy/docker-compose.p16a-test.yml),
/// so a folder picked here is exactly the path the supervisor will resolve
/// a recipe's model path against.</summary>
public sealed class LocalFilesystemBrowser : IFilesystemBrowser
{
    public string RootPath { get; }

    public LocalFilesystemBrowser(IConfiguration configuration)
    {
        RootPath = Path.GetFullPath(configuration["MODELS_BROWSE_ROOT"] ?? "/models");
    }

    public bool IsAvailable => Directory.Exists(RootPath);

    public FolderBrowseResult ListFolders(string? path)
    {
        var target = ResolveWithinRoot(path);

        var subfolders = new List<FolderEntry>();
        if (Directory.Exists(target))
        {
            foreach (var dir in Directory.EnumerateDirectories(target).OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
            {
                subfolders.Add(new FolderEntry(Path.GetFileName(dir), dir));
            }
        }

        var isRoot = string.Equals(target.TrimEnd('/'), RootPath.TrimEnd('/'), StringComparison.Ordinal);
        string? parent = isRoot ? null : Path.GetDirectoryName(target.TrimEnd('/'));

        return new FolderBrowseResult
        {
            CurrentPath = target,
            CanGoUp = parent is not null,
            ParentPath = parent,
            Subfolders = subfolders,
        };
    }

    /// <summary>Clamps any requested path back inside <see cref="RootPath"/> -- a
    /// stale ".." breadcrumb, an edited DB value, or a plain bug can't walk this
    /// browser outside the one directory it was mounted to see.</summary>
    private string ResolveWithinRoot(string? requested)
    {
        if (string.IsNullOrWhiteSpace(requested))
            return RootPath;

        var full = Path.GetFullPath(requested);
        var rootWithSep = RootPath.TrimEnd('/') + '/';
        if (full.Equals(RootPath, StringComparison.Ordinal) || full.StartsWith(rootWithSep, StringComparison.Ordinal))
            return full;

        return RootPath;
    }
}
