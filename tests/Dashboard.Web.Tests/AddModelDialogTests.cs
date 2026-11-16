using Bunit;
using Dashboard.Core.Abstractions;
using Dashboard.Core.Supervisor;
using Dashboard.Infrastructure.Persistence.V2;
using Dashboard.Web.Components.ModelsRecipes;
using Microsoft.Extensions.DependencyInjection;

namespace Dashboard.Web.Tests;

public class AddModelDialogTests : BunitContext
{
    private static Instance TestInstance() => new() { Id = 1, ServerId = 1, Label = "i", ProxySlug = "i", AdminUrl = "http://host:8000/admin" };

    private (IRenderedComponent<AddModelDialog> Cut, List<(string RepoId, string Slug, IReadOnlyList<string>? Filenames)> StartCalls, List<Model> Saved)
        Render(
            SupervisorDownloadInspectResult inspectResult,
            Func<object?[], object?>? startDownload = null,
            Func<object?[], object?>? getDownload = null)
    {
        var startCalls = new List<(string, string, IReadOnlyList<string>?)>();
        Services.AddSingleton(ProxyStub.Create<ISupervisorClient>(h =>
        {
            h["InspectDownloadAsync"] = _ => Task.FromResult(inspectResult);
            h["StartDownloadAsync"] = args =>
            {
                var repoId = (string)args[1]!;
                var slug = (string)args[2]!;
                var filenames = (IReadOnlyList<string>?)args[3];
                startCalls.Add((repoId, slug, filenames));
                return startDownload?.Invoke(args) ?? Task.FromResult("dl-1");
            };
            h["GetDownloadAsync"] = args => getDownload?.Invoke(args) ?? Task.FromResult(new SupervisorDownloadStatus { Status = "done" });
        }));
        Services.AddSingleton(ProxyStub.Create<IHuggingFaceModelLookup>());
        var saved = new List<Model>();
        Services.AddSingleton(ProxyStub.Create<IModelService>(h => h["CreateAsync"] = args =>
        {
            var m = (Model)args[0]!;
            saved.Add(m);
            return Task.FromResult(m);
        }));
        JSInterop.Mode = JSRuntimeMode.Loose;

        var cut = Render<AddModelDialog>(p => p.Add(c => c.Instance, TestInstance()));
        cut.Find("#am-repo-id").Input("org/repo");
        cut.FindAll("button").First(b => b.TextContent.Trim() == "Inspect").Click();
        return (cut, startCalls, saved);
    }

    private static SupervisorDownloadInspectResult PlainRepoResult() => new()
    {
        Files = [new SupervisorRepoFile { Path = "model.safetensors", Size = 1000 }],
        FreeBytes = long.MaxValue,
    };

    private static SupervisorDownloadInspectResult MultiQuantResult() => new()
    {
        Files =
        [
            new SupervisorRepoFile { Path = "model.Q4_K_M.gguf", Size = 1024 },
            new SupervisorRepoFile { Path = "model-00001-of-00002.gguf", Size = 2048 },
            new SupervisorRepoFile { Path = "model-00002-of-00002.gguf", Size = 2048 },
        ],
        FreeBytes = long.MaxValue,
        QuantGroups =
        [
            new SupervisorQuantGroup { Name = "model.Q4_K_M", Files = ["model.Q4_K_M.gguf"], TotalBytes = 1024 },
            new SupervisorQuantGroup { Name = "model", Files = ["model-00001-of-00002.gguf", "model-00002-of-00002.gguf"], TotalBytes = 4096 },
        ],
    };

    [Fact]
    public void Inspect_WithOnlyOneQuantGroup_ShowsNoPicker_AndDownloadEnabledImmediately()
    {
        var (cut, _, _) = Render(PlainRepoResult());
        cut.Find("#am-name").Input("my-model");

        Assert.DoesNotContain("Quants to download", cut.Markup);
        var download = cut.FindAll("button").First(b => b.TextContent.Trim() == "Download");
        Assert.False(download.HasAttribute("disabled"));
    }

    [Fact]
    public void Inspect_WithMultipleQuantGroups_ShowsACheckboxPerGroup_AndDownloadDisabledUntilOneChecked()
    {
        var (cut, _, _) = Render(MultiQuantResult());
        cut.Find("#am-name").Input("my-model");

        Assert.Contains("Quants to download", cut.Markup);
        var checkboxes = cut.FindAll("input[type=checkbox]");
        Assert.Equal(2, checkboxes.Count);
        var download = cut.FindAll("button").First(b => b.TextContent.Trim() == "Download");
        Assert.True(download.HasAttribute("disabled"));

        checkboxes[0].Change(true);

        download = cut.FindAll("button").First(b => b.TextContent.Trim() == "Download");
        Assert.False(download.HasAttribute("disabled"));
    }

    [Fact]
    public void Download_WithNoQuantPicker_SendsNullFilenames()
    {
        var (cut, startCalls, _) = Render(PlainRepoResult());
        cut.Find("#am-name").Input("my-model");

        cut.FindAll("button").First(b => b.TextContent.Trim() == "Download").Click();

        var call = Assert.Single(startCalls);
        Assert.Null(call.Filenames);
    }

    [Fact]
    public void Download_WithASelectedShardedQuant_SendsAllItsShardFilenames_NotJustTheRepresentative()
    {
        var (cut, startCalls, _) = Render(MultiQuantResult());
        cut.Find("#am-name").Input("my-model");

        // Select only the sharded group ("model"), not the single-file one.
        cut.FindAll("input[type=checkbox]")[1].Change(true);
        cut.FindAll("button").First(b => b.TextContent.Trim() == "Download").Click();

        var call = Assert.Single(startCalls);
        Assert.NotNull(call.Filenames);
        Assert.Equal(["model-00001-of-00002.gguf", "model-00002-of-00002.gguf"], call.Filenames);
    }

    [Fact]
    public void FinishDownload_WithMultipleCandidates_UsesThePickedCandidate_AndMarksLoadFormatGguf()
    {
        var status = new SupervisorDownloadStatus
        {
            Status = "done",
            Path = "/models/my-model",
            CandidateFiles =
            [
                new SupervisorCandidateFile { Path = "/models/my-model/model.Q4_K_M.gguf", Size = 1024 },
                new SupervisorCandidateFile { Path = "/models/my-model/model-00001-of-00002.gguf", Size = 4096 },
            ],
        };
        var (cut, _, saved) = Render(MultiQuantResult(), getDownload: _ => Task.FromResult(status));
        cut.Find("#am-name").Input("my-model");
        cut.FindAll("input[type=checkbox]")[0].Change(true);
        cut.FindAll("button").First(b => b.TextContent.Trim() == "Download").Click();

        cut.WaitForAssertion(() => Assert.Contains("Add to catalog", cut.Markup));
        Assert.Contains("2 quants downloaded", cut.Markup);

        cut.Find("#am-candidate").Change("/models/my-model/model-00001-of-00002.gguf");
        cut.FindAll("button").First(b => b.TextContent.Trim() == "Add to catalog").Click();

        var model = Assert.Single(saved);
        Assert.Equal("/models/my-model/model-00001-of-00002.gguf", model.Path);
        Assert.Equal(4096, model.SizeBytes);
        Assert.Equal("gguf", model.LoadFormat);
    }

    [Fact]
    public void FinishDownload_WithNoCandidates_UsesTheStatusPathAndLoadFormat_Unchanged()
    {
        var status = new SupervisorDownloadStatus
        {
            Status = "done", Path = "/models/my-model", SizeBytes = 999, LoadFormat = "auto",
        };
        var (cut, _, saved) = Render(PlainRepoResult(), getDownload: _ => Task.FromResult(status));
        cut.Find("#am-name").Input("my-model");
        cut.FindAll("button").First(b => b.TextContent.Trim() == "Download").Click();

        cut.WaitForAssertion(() => Assert.Contains("Add to catalog", cut.Markup));
        Assert.DoesNotContain("quants downloaded", cut.Markup);

        cut.FindAll("button").First(b => b.TextContent.Trim() == "Add to catalog").Click();

        var model = Assert.Single(saved);
        Assert.Equal("/models/my-model", model.Path);
        Assert.Equal(999, model.SizeBytes);
        Assert.Equal("auto", model.LoadFormat);
    }
}
