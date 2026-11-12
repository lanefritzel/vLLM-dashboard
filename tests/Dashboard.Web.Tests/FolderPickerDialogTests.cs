using Bunit;
using Dashboard.Core.Abstractions;
using Dashboard.Web.Components.Shared;
using Microsoft.Extensions.DependencyInjection;

namespace Dashboard.Web.Tests;

/// <summary>AllowFiles is opt-in (default false) so the Server.ModelsDir picker (which
/// never passes it) keeps today's folder-only behavior unchanged; only the model
/// weights-path pickers (EditModelDialog, AddModelDialog) turn it on.</summary>
public class FolderPickerDialogTests : BunitContext
{
    private sealed class FakeBrowser : IFilesystemBrowser
    {
        public bool IsAvailable { get; init; } = true;
        public string RootPath { get; init; } = "/models";
        public FolderBrowseResult Result { get; init; } = null!;
        public FolderBrowseResult ListFolders(string? path) => Result;
    }

    private static FolderBrowseResult ResultWith(IReadOnlyList<FolderEntry>? subfolders = null, IReadOnlyList<FolderEntry>? files = null) =>
        new()
        {
            CurrentPath = "/models/qwen3-8B",
            CanGoUp = true,
            ParentPath = "/models",
            Subfolders = subfolders ?? [],
            Files = files ?? [],
        };

    private IRenderedComponent<FolderPickerDialog> Render(FolderBrowseResult result, bool allowFiles, out List<string> selected)
    {
        Services.AddSingleton<IFilesystemBrowser>(new FakeBrowser { Result = result });
        JSInterop.Mode = JSRuntimeMode.Loose;
        var captured = new List<string>();
        selected = captured;
        return base.Render<FolderPickerDialog>(p => p
            .Add(c => c.AllowFiles, allowFiles)
            .Add(c => c.OnSelected, (string s) => captured.Add(s)));
    }

    [Fact]
    public void WhenAllowFilesIsFalse_FileEntriesAreNeverRendered()
    {
        var result = ResultWith(files: [new FolderEntry("model.gguf", "/models/qwen3-8B/model.gguf")]);

        var cut = Render(result, allowFiles: false, out _);

        Assert.DoesNotContain("model.gguf", cut.Markup);
    }

    [Fact]
    public void WhenAllowFilesIsTrue_FileEntriesRenderAlongsideFolders()
    {
        var result = ResultWith(
            subfolders: [new FolderEntry("subdir", "/models/qwen3-8B/subdir")],
            files: [new FolderEntry("Qwen_Qwen3-8B-Q4_K_M.gguf", "/models/qwen3-8B/Qwen_Qwen3-8B-Q4_K_M.gguf")]);

        var cut = Render(result, allowFiles: true, out _);

        Assert.Contains("subdir", cut.Markup);
        Assert.Contains("Qwen_Qwen3-8B-Q4_K_M.gguf", cut.Markup);
    }

    [Fact]
    public void ClickingAFile_SelectsItsFullPath_WithoutNavigatingIntoIt()
    {
        var result = ResultWith(files: [new FolderEntry("model.gguf", "/models/qwen3-8B/model.gguf")]);

        var cut = Render(result, allowFiles: true, out var selected);
        cut.FindAll("button").First(b => b.TextContent.Contains("model.gguf")).Click();

        Assert.Equal("/models/qwen3-8B/model.gguf", Assert.Single(selected));
    }
}
