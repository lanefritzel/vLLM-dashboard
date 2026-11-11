using Bunit;
using Dashboard.Core.Models;
using Dashboard.Infrastructure.Persistence.V2;
using Dashboard.Web.Components.ModelsRecipes;
using Dashboard.Web.Components.Shared;
using Microsoft.Extensions.DependencyInjection;

namespace Dashboard.Web.Tests;

/// <summary>P5: the recipe editor shows only the dials the target engine has -- one form, with the
/// vLLM-only sections hidden for llama.cpp (the UI counterpart of LlamaCppRecipe having no such
/// properties). The vLLM form is unchanged.</summary>
public class RecipeEditorDialogTests : BunitContext
{
    private static readonly string[] VllmOnlyIds =
    [
        "rc-gpu-mem", "rc-kv-dtype", "rc-dtype", "rc-max-batched", "rc-block-size", "rc-tool-parser", "rc-reasoning-parser",
    ];

    private static readonly string[] VllmOnlyCheckboxes = ["Enforce eager", "Speculative decoding", "Auto tool choice"];

    private readonly List<SourceType?> _validatedWith = new();
    private readonly List<Recipe> _saved = new();

    private IRenderedComponent<RecipeEditorDialog> RenderEditor(SourceType engine, Recipe? recipe = null, bool isNew = false)
    {
        Services.AddSingleton(ProxyStub.Create<IRecipeService>(h =>
        {
            h["ValidateDraftAsync"] = a => { _validatedWith.Add((SourceType?)a[2]); return Task.FromResult(new Dashboard.Core.Recipes.CompatibilityResult()); };
            h["UpdateAsync"] = a => { var r = (Recipe)a[0]!; _saved.Add(r); return Task.FromResult(r); };
            h["CreateAsync"] = a => { var r = (Recipe)a[0]!; _saved.Add(r); return Task.FromResult(r); };
        }));
        JSInterop.Mode = JSRuntimeMode.Loose;
        return Render<RecipeEditorDialog>(p => p
            .Add(c => c.Model, recipe ?? new Recipe { Id = 4, ServerId = 1, Name = "r", ServedModelName = "main", MaxModelLen = 4096 })
            .Add(c => c.IsNew, isNew)
            .Add(c => c.Engine, engine));
    }

    [Fact]
    public void Vllm_ShowsEveryDial_AsToday()
    {
        var cut = RenderEditor(SourceType.Vllm);
        foreach (var id in VllmOnlyIds)
            Assert.NotEmpty(cut.FindAll("#" + id));
        foreach (var label in VllmOnlyCheckboxes)
            Assert.Contains(label, cut.Markup);
        Assert.Contains("Sizing &amp; memory", cut.Markup);
        Assert.Contains("Max model length", cut.Markup);
    }

    [Fact]
    public void LlamaCpp_HidesEveryVllmOnlyDial()
    {
        var cut = RenderEditor(SourceType.LlamaCpp);
        foreach (var id in VllmOnlyIds)
            Assert.Empty(cut.FindAll("#" + id));
        foreach (var label in VllmOnlyCheckboxes)
            Assert.DoesNotContain(label, cut.Markup);
        Assert.DoesNotContain("Tools &amp; reasoning", cut.Markup);
        Assert.DoesNotContain("Performance", cut.Markup);
    }

    [Fact]
    public void LlamaCpp_StillShowsTheSharedFields_NamedAfterItsOwnFlags()
    {
        var cut = RenderEditor(SourceType.LlamaCpp);
        foreach (var id in new[] { "rc-name", "rc-description", "rc-served-name", "rc-max-len" })
            Assert.NotEmpty(cut.FindAll("#" + id));
        Assert.Contains("--ctx-size", cut.Markup);
        Assert.Contains("--alias", cut.Markup);
        Assert.DoesNotContain("--max-model-len", cut.Markup);
        Assert.Contains("Extra args", cut.Markup);
        Assert.Contains("Environment vars", cut.Markup);
    }

    [Fact]
    public void Validation_IsAskedForTheEditorsEngine()
    {
        RenderEditor(SourceType.LlamaCpp);
        Assert.Equal(SourceType.LlamaCpp, Assert.Single(_validatedWith));
    }

    [Fact]
    public void SavingALlamaCppRecipe_LeavesTheHiddenVllmValuesUntouched()
    {
        // A recipe isn't tied to one engine; hiding a dial must not reset what a vLLM instance may still use.
        var recipe = new Recipe
        {
            Id = 4, ServerId = 1, Name = "r", ServedModelName = "main", MaxModelLen = 4096,
            GpuMemoryUtilization = 0.5f, Dtype = "float16", BlockSize = 32, ToolCallParser = "qwen3_coder",
        };
        var cut = RenderEditor(SourceType.LlamaCpp, recipe);
        cut.Find("#rc-name").Input("renamed");
        cut.Find("form#recipe-form").Submit();

        var saved = Assert.Single(_saved);
        Assert.Equal("renamed", saved.Name);
        Assert.Equal(0.5f, saved.GpuMemoryUtilization);
        Assert.Equal("float16", saved.Dtype);
        Assert.Equal(32, saved.BlockSize);
        Assert.Equal("qwen3_coder", saved.ToolCallParser);
    }
}
