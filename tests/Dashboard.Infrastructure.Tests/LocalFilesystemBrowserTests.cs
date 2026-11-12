using Dashboard.Infrastructure.Filesystem;
using Microsoft.Extensions.Configuration;

namespace Dashboard.Infrastructure.Tests;

/// <summary>Only .gguf ever comes back as a selectable file -- .safetensors/.bin
/// shards (and anything else) are deliberately invisible here, since they're never
/// valid as a standalone model path for either engine. See IFilesystemBrowser's
/// own doc comment for why this isn't engine-specific.</summary>
public class LocalFilesystemBrowserTests : IDisposable
{
    private readonly string _root;

    public LocalFilesystemBrowserTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "fsbrowser-tests-" + Guid.NewGuid());
        Directory.CreateDirectory(_root);
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private LocalFilesystemBrowser NewSut()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["MODELS_BROWSE_ROOT"] = _root })
            .Build();
        return new LocalFilesystemBrowser(config);
    }

    [Fact]
    public void ListFolders_ListsGgufFiles_ButNotOtherExtensions()
    {
        var modelDir = Path.Combine(_root, "qwen3-8B");
        Directory.CreateDirectory(modelDir);
        File.WriteAllText(Path.Combine(modelDir, "Qwen_Qwen3-8B-Q4_K_M.gguf"), "");
        File.WriteAllText(Path.Combine(modelDir, "Qwen_Qwen3-8B-Q8_0.gguf"), "");
        File.WriteAllText(Path.Combine(modelDir, "README.md"), "");
        File.WriteAllText(Path.Combine(modelDir, "model-00001-of-00004.safetensors"), "");

        var result = NewSut().ListFolders(modelDir);

        Assert.Equal(
            new[] { "Qwen_Qwen3-8B-Q4_K_M.gguf", "Qwen_Qwen3-8B-Q8_0.gguf" },
            result.Files.Select(f => f.Name).OrderBy(n => n));
    }

    [Fact]
    public void ListFolders_FileFullPath_IsTheRealOnDiskPath()
    {
        var modelDir = Path.Combine(_root, "qwen3-8B");
        Directory.CreateDirectory(modelDir);
        var ggufPath = Path.Combine(modelDir, "Qwen_Qwen3-8B-Q4_K_M.gguf");
        File.WriteAllText(ggufPath, "");

        var result = NewSut().ListFolders(modelDir);

        Assert.Equal(ggufPath, Assert.Single(result.Files).FullPath);
    }

    [Fact]
    public void ListFolders_StillListsSubfoldersAlongsideFiles()
    {
        var modelDir = Path.Combine(_root, "qwen3-8B");
        Directory.CreateDirectory(modelDir);
        Directory.CreateDirectory(Path.Combine(modelDir, ".cache"));
        File.WriteAllText(Path.Combine(modelDir, "Qwen_Qwen3-8B-Q4_K_M.gguf"), "");

        var result = NewSut().ListFolders(modelDir);

        Assert.Single(result.Subfolders);
        Assert.Single(result.Files);
    }

    [Fact]
    public void ListFolders_OnAnEmptyFolder_ReturnsBothListsEmpty_NotNull()
    {
        var modelDir = Path.Combine(_root, "empty");
        Directory.CreateDirectory(modelDir);

        var result = NewSut().ListFolders(modelDir);

        Assert.Empty(result.Subfolders);
        Assert.Empty(result.Files);
    }
}
