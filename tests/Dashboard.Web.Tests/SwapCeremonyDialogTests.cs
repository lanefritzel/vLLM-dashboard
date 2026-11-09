using Bunit;
using Dashboard.Core.Abstractions;
using Dashboard.Infrastructure.Persistence.V2;
using Dashboard.Web.Components.Overview;
using Dashboard.Web.Components.Shared;
using Microsoft.Extensions.DependencyInjection;

namespace Dashboard.Web.Tests;

public class SwapCeremonyDialogTests : BunitContext
{
    private static Model M(int id, string name) => new() { Id = id, ServerId = 1, Name = name, Path = "/models/" + name };
    private static Recipe R(int id, string name) => new() { Id = id, ServerId = 1, Name = name, ServedModelName = "main" };

    private void RegisterStubs(Func<object?[], object?> listModels, Func<object?[], object?> listRecipes)
    {
        Services.AddSingleton(ProxyStub.Create<IModelService>(h => h["ListByServerAsync"] = listModels));
        Services.AddSingleton(ProxyStub.Create<IRecipeService>(h => h["ListByServerAsync"] = listRecipes));
        Services.AddSingleton(ProxyStub.Create<IDeploymentService>());
        Services.AddSingleton(ProxyStub.Create<ISwapService>());
        Services.AddSingleton(ProxyStub.Create<ISupervisorClient>());
        Services.AddSingleton(ProxyStub.Create<IBootReconciler>());
        Services.AddSingleton(ProxyStub.Create<IDialogService>());
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    private IRenderedComponent<SwapCeremonyDialog> RenderDialog(List<Model> pageModels, List<Recipe> pageRecipes) =>
        Render<SwapCeremonyDialog>(p => p
            .Add(c => c.Instance, new Instance { Id = 1, ServerId = 1, Label = "i", ProxySlug = "i" })
            .Add(c => c.Models_, pageModels)
            .Add(c => c.Recipes_, pageRecipes));

    private IRenderedComponent<SwapCeremonyDialog> RenderProgress(string logText)
    {
        Services.AddSingleton(ProxyStub.Create<IModelService>());
        Services.AddSingleton(ProxyStub.Create<IRecipeService>());
        Services.AddSingleton(ProxyStub.Create<IDeploymentService>(h =>
        {
            h["GetAsync"] = _ => Task.FromResult<Deployment?>(new Deployment { Id = 5, InstanceId = 1, Status = "pending" });
            h["ListEventsAsync"] = _ => Task.FromResult<IReadOnlyList<DeploymentEvent>>([]);
        }));
        Services.AddSingleton(ProxyStub.Create<ISwapService>());
        Services.AddSingleton(ProxyStub.Create<ISupervisorClient>(h =>
            h["GetDeploymentLogsAsync"] = _ => Task.FromResult(logText)));
        Services.AddSingleton(ProxyStub.Create<IBootReconciler>());
        Services.AddSingleton(ProxyStub.Create<IDialogService>());
        JSInterop.Mode = JSRuntimeMode.Loose;

        return Render<SwapCeremonyDialog>(p => p
            .Add(c => c.Instance, new Instance { Id = 1, ServerId = 1, Label = "i", ProxySlug = "i", AdminUrl = "http://sup.invalid" })
            .Add(c => c.Models_, new List<Model>())
            .Add(c => c.Recipes_, new List<Recipe>())
            .Add(c => c.ResumeDeploymentId, 5));
    }

    [Fact]
    public void ProgressLog_ScrollsToTheBottom_WhenLinesArrive()
    {
        var cut = RenderProgress("loading weights 10%\nloading weights 50%\nOOM");

        cut.WaitForAssertion(() =>
        {
            Assert.Contains("OOM", cut.Find("#ceremony-log").TextContent);
            Assert.NotEmpty(JSInterop.Invocations["ui_scrollToBottom"]);
        });
    }

    [Fact]
    public void Picker_DoesNotTryToScrollAnyLog()
    {
        RegisterStubs(
            _ => Task.FromResult<IReadOnlyList<Model>>([M(1, "m")]),
            _ => Task.FromResult<IReadOnlyList<Recipe>>([R(1, "r")]));

        RenderDialog([M(1, "m")], [R(1, "r")]);

        Assert.DoesNotContain(JSInterop.Invocations, i => i.Identifier == "ui_scrollToBottom");
    }

    [Fact]
    public void Picker_ShowsModelsAndRecipesAddedSinceThePageLoaded()
    {
        // Overview loaded with only "old"; "new" (and a new recipe) were added elsewhere since.
        RegisterStubs(
            _ => Task.FromResult<IReadOnlyList<Model>>([M(1, "old"), M(2, "new-gemma")]),
            _ => Task.FromResult<IReadOnlyList<Recipe>>([R(1, "r1"), R(2, "r2-new")]));

        var cut = RenderDialog([M(1, "old")], [R(1, "r1")]);

        var options = cut.FindAll("option").Select(o => o.TextContent).ToList();
        Assert.Contains("new-gemma", options);
        Assert.Contains("r2-new", options);
    }

    [Fact]
    public void Picker_FallsBackToThePagesLists_WhenTheLiveReadFails()
    {
        RegisterStubs(
            _ => throw new InvalidOperationException("db down"),
            _ => throw new InvalidOperationException("db down"));

        var cut = RenderDialog([M(1, "old")], [R(1, "r1")]);

        var options = cut.FindAll("option").Select(o => o.TextContent).ToList();
        Assert.Contains("old", options);
        Assert.Contains("r1", options);
    }
}
