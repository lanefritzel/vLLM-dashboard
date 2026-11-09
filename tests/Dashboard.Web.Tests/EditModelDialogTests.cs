using Bunit;
using Dashboard.Infrastructure.Persistence.V2;
using Dashboard.Web.Components.ModelsRecipes;
using Microsoft.Extensions.DependencyInjection;

namespace Dashboard.Web.Tests;

public class EditModelDialogTests : BunitContext
{
    private static Model GgufModel() => new()
    {
        Id = 7, ServerId = 1, Name = "gemma", Path = "/models/gemma/dir", LoadFormat = "auto",
        SourceType = "huggingface", RepoId = "org/gemma", SizeBytes = 123, Quantization = "Q4_K_M", Status = "available",
    };

    private (IRenderedComponent<EditModelDialog> Cut, List<Model> Updated, List<Model> Saved, List<bool> Cancelled) Render(
        Model model, Func<Model, Task<Model>>? update = null)
    {
        var updated = new List<Model>();
        Services.AddSingleton(ProxyStub.Create<IModelService>(h => h["UpdateAsync"] = args =>
        {
            var m = (Model)args[0]!;
            updated.Add(m);
            return update is null ? Task.FromResult(m) : update(m);
        }));
        JSInterop.Mode = JSRuntimeMode.Loose;

        var saved = new List<Model>();
        var cancelled = new List<bool>();
        var cut = Render<EditModelDialog>(p => p
            .Add(c => c.Model, model)
            .Add(c => c.OnSaved, (Model m) => saved.Add(m))
            .Add(c => c.OnCancelled, () => cancelled.Add(true)));
        return (cut, updated, saved, cancelled);
    }

    [Fact]
    public void PrefillsNamePathAndLoadFormatFromTheModel()
    {
        var (cut, _, _, _) = Render(GgufModel());

        Assert.Equal("gemma", cut.Find("#em-name").GetAttribute("value"));
        Assert.Equal("/models/gemma/dir", cut.Find("#em-path").GetAttribute("value"));
        Assert.Equal("auto", cut.Find("#em-load-format").GetAttribute("value"));
    }

    [Fact]
    public void Save_SendsEditedPathAndLoadFormat_AndKeepsTheRestOfTheRow()
    {
        var original = GgufModel();
        var (cut, updated, saved, _) = Render(original);

        cut.Find("#em-path").Input("  /models/gemma/gemma-q4.gguf  ");
        cut.Find("#em-load-format").Change("gguf");
        cut.FindAll("button").First(b => b.TextContent.Trim() == "Save").Click();

        var sent = Assert.Single(updated);
        Assert.Equal(7, sent.Id);
        Assert.Equal("/models/gemma/gemma-q4.gguf", sent.Path); // trimmed
        Assert.Equal("gguf", sent.LoadFormat);
        // Untouched fields ride along, so the UPDATE doesn't blank them.
        Assert.Equal("huggingface", sent.SourceType);
        Assert.Equal("org/gemma", sent.RepoId);
        Assert.Equal(123, sent.SizeBytes);
        Assert.Equal("Q4_K_M", sent.Quantization);
        Assert.Equal("available", sent.Status);
        Assert.Single(saved);
        // The page's own row object is not mutated by editing.
        Assert.Equal("/models/gemma/dir", original.Path);
        Assert.Equal("auto", original.LoadFormat);
    }

    [Fact]
    public void Save_IsDisabled_WhenNameOrPathIsBlank()
    {
        var (cut, updated, _, _) = Render(GgufModel());

        cut.Find("#em-path").Input("   ");

        var save = cut.FindAll("button").First(b => b.TextContent.Trim() == "Save");
        Assert.True(save.HasAttribute("disabled"));
        Assert.Empty(updated);
    }

    [Fact]
    public void Save_ShowsTheError_AndDoesNotReportSaved_WhenTheServiceThrows()
    {
        var (cut, _, saved, _) = Render(GgufModel(), _ => throw new ArgumentException("A model must have a weights path."));

        cut.FindAll("button").First(b => b.TextContent.Trim() == "Save").Click();

        Assert.Contains("Save failed: A model must have a weights path.", cut.Markup);
        Assert.Empty(saved);
    }

    [Fact]
    public void Cancel_ReportsCancelled_WithoutSaving()
    {
        var (cut, updated, _, cancelled) = Render(GgufModel());

        cut.FindAll("button").First(b => b.TextContent.Trim() == "Cancel").Click();

        Assert.Single(cancelled);
        Assert.Empty(updated);
    }
}
