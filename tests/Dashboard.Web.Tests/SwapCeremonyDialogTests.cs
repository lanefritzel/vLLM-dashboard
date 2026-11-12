using Bunit;
using Dashboard.Core.Abstractions;
using Dashboard.Infrastructure.Persistence.V2;
using Dashboard.Web.Components.Overview;
using Dashboard.Web.Components.Shared;
using Microsoft.Extensions.DependencyInjection;

namespace Dashboard.Web.Tests;

public class SwapCeremonyDialogTests : BunitContext
{
    private string _engine = "vllm";

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
            .Add(c => c.Instance, new Instance { Id = 1, ServerId = 1, Label = "i", ProxySlug = "i", AdminUrl = "http://sup.invalid", Engine = _engine })
            .Add(c => c.Models_, new List<Model>())
            .Add(c => c.Recipes_, new List<Recipe>())
            .Add(c => c.ResumeDeploymentId, 5));
    }

    private IRenderedComponent<SwapCeremonyDialog> RenderProgressWithEvents(params string[] states) =>
        RenderProgressWithEvents(states.Select(st => (st, (string?)null)).ToArray());

    private IRenderedComponent<SwapCeremonyDialog> RenderProgressWithEvents(params (string State, string? Detail)[] evts)
    {
        var events = evts.Select((e, i) => new DeploymentEvent { DeploymentId = 5, Seq = i + 1, State = e.State, Detail = e.Detail }).ToList();
        Services.AddSingleton(ProxyStub.Create<IModelService>());
        Services.AddSingleton(ProxyStub.Create<IRecipeService>());
        Services.AddSingleton(ProxyStub.Create<IDeploymentService>(h =>
        {
            h["GetAsync"] = _ => Task.FromResult<Deployment?>(new Deployment { Id = 5, InstanceId = 1, Status = "pending" });
            h["ListEventsAsync"] = _ => Task.FromResult<IReadOnlyList<DeploymentEvent>>(events);
        }));
        Services.AddSingleton(ProxyStub.Create<ISwapService>());
        Services.AddSingleton(ProxyStub.Create<ISupervisorClient>(h => h["GetDeploymentLogsAsync"] = _ => Task.FromResult("")));
        Services.AddSingleton(ProxyStub.Create<IBootReconciler>());
        Services.AddSingleton(ProxyStub.Create<IDialogService>());
        JSInterop.Mode = JSRuntimeMode.Loose;

        return Render<SwapCeremonyDialog>(p => p
            .Add(c => c.Instance, new Instance { Id = 1, ServerId = 1, Label = "i", ProxySlug = "i", AdminUrl = "http://sup.invalid", Engine = _engine })
            .Add(c => c.Models_, new List<Model>())
            .Add(c => c.Recipes_, new List<Recipe>())
            .Add(c => c.ResumeDeploymentId, 5));
    }

    /// <summary>The six step labels in order -> their css state (step-done/current/failed/pending).</summary>
    private static string[] StepClasses(IRenderedComponent<SwapCeremonyDialog> cut) =>
        cut.FindAll("ol.ceremony-steps li").Select(li => li.ClassList.First(c => c.StartsWith("step-"))).ToArray();

    [Fact]
    public void Steps_FollowTheSupervisorsEvents_FirstStepIncluded()
    {
        // "stopping" is the supervisor's own name for step 1; it used to never match.
        var cut = RenderProgressWithEvents("stopping");
        Assert.Equal(["step-current", "step-pending", "step-pending", "step-pending", "step-pending", "step-pending", "step-pending"], StepClasses(cut));
    }

    [Fact]
    public void Steps_EarlierOnesAreDone_AndTheLatestIsCurrent()
    {
        var cut = RenderProgressWithEvents("stopping", "releasing_gpu", "starting", "loading", "warming_up");
        Assert.Equal(["step-done", "step-done", "step-done", "step-done", "step-current", "step-pending", "step-pending"], StepClasses(cut));
    }

    [Fact]
    public void Steps_WhileVllmIsStillImporting_WeightsAreNotShownAsLoading()
    {
        // The supervisor only reports "loading" once vLLM says it is loading the model;
        // before that the process is merely starting.
        var cut = RenderProgressWithEvents("stopping", "releasing_gpu", "starting");
        Assert.Equal(["step-done", "step-done", "step-current", "step-pending", "step-pending", "step-pending", "step-pending"], StepClasses(cut));
    }

    [Fact]
    public void Steps_AReadyDeploy_NeverShowsAFailedStep_EvenBeforeTheRowSettles()
    {
        // Regression: after "ready" but before the row flipped to active, the last step was painted red.
        var cut = RenderProgressWithEvents("stopping", "releasing_gpu", "starting", "loading", "warming_up", "waiting_health", "ready");
        var classes = StepClasses(cut);
        Assert.DoesNotContain("step-failed", classes);
        Assert.Equal(["step-done", "step-done", "step-done", "step-done", "step-done", "step-done", "step-pending"], classes);
    }

    [Fact]
    public void Steps_AFailedDeploy_MarksTheStepItFailedInAsFailed()
    {
        var cut = RenderProgressWithEvents("stopping", "releasing_gpu", "starting", "loading", "failed");
        Assert.Equal(["step-done", "step-done", "step-done", "step-failed", "step-pending", "step-pending", "step-pending"], StepClasses(cut));
    }

    private static string[] StepTexts(IRenderedComponent<SwapCeremonyDialog> cut) =>
        cut.FindAll("ol.ceremony-steps li").Select(li => li.TextContent.Trim()).ToArray();

    [Fact]
    public void LoadingStep_ShowsTheRealShardProgressVllmReported()
    {
        var cut = RenderProgressWithEvents(
            ("starting", null), ("loading", """{"weights_pct":0,"shards":"0/2"}"""), ("loading", """{"weights_pct":50,"shards":"1/2"}"""));

        Assert.Equal("Load weights — 50% · shard 1/2", StepTexts(cut)[3]);
    }

    [Fact]
    public void WarmupStep_ShowsWhatVllmIsDoing_AndCudaGraphProgress()
    {
        var cut = RenderProgressWithEvents(
            ("loading", """{"weights_pct":100,"shards":"1/1"}"""),
            ("warming_up", """{"graphs_pct":40,"note":"capturing CUDA graphs"}"""));

        Assert.Equal("Warm up engine — CUDA graphs 40% · capturing CUDA graphs", StepTexts(cut)[4]);
        Assert.Equal("Load weights", StepTexts(cut)[3]); // finished steps carry no progress text
    }

    [Fact]
    public void StartingStep_ShowsVllmsOwnNote()
    {
        var cut = RenderProgressWithEvents(("starting", null), ("starting", """{"note":"engine core starting"}"""));

        Assert.Equal("Start new process — engine core starting", StepTexts(cut)[2]);
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

    // ---- llama.cpp: three flat stages, no warm-up phase (Findings 4) ----

    [Fact]
    public void LlamaCpp_Steps_HaveNoWarmupRow_AndLoadingIsLabelledAsTheModel()
    {
        _engine = "llamacpp";
        var cut = RenderProgressWithEvents("stopping");
        var labels = cut.FindAll("ol.ceremony-steps li").Select(li => li.TextContent.Trim()).ToArray();
        Assert.Equal(6, labels.Length);
        Assert.DoesNotContain(labels, l => l.Contains("Warm up"));
        Assert.Contains(labels, l => l.Contains("Load model"));
    }

    [Fact]
    public void LlamaCpp_Steps_NothingIsFalselyTickedForAPhaseThatNeverRan()
    {
        _engine = "llamacpp";
        var cut = RenderProgressWithEvents("stopping", "releasing_gpu", "starting", "loading", "waiting_health");
        Assert.Equal(["step-done", "step-done", "step-done", "step-done", "step-current", "step-pending"], StepClasses(cut));
    }

    [Fact]
    public void LlamaCpp_Steps_AReadyDeployCompletesEveryRowItHas()
    {
        _engine = "llamacpp";
        var cut = RenderProgressWithEvents("stopping", "releasing_gpu", "starting", "loading", "waiting_health", "ready");
        var classes = StepClasses(cut);
        Assert.DoesNotContain("step-failed", classes);
        Assert.Equal(["step-done", "step-done", "step-done", "step-done", "step-done", "step-pending"], classes);
    }

    [Fact]
    public void LlamaCpp_LoadingStep_ShowsItsNote_AndNeverAPercentage()
    {
        _engine = "llamacpp";
        var cut = RenderProgressWithEvents(("starting", null), ("loading", """{"note":"allocating kv cache slots"}"""));
        var loading = StepTexts(cut).Single(t => t.Contains("Load model"));
        Assert.Contains("allocating kv cache slots", loading);
        Assert.DoesNotContain("%", loading);
    }

    [Fact]
    public void LlamaCpp_AFailedLoad_MarksTheLoadRowFailed()
    {
        _engine = "llamacpp";
        var cut = RenderProgressWithEvents("stopping", "releasing_gpu", "starting", "loading", "failed");
        Assert.Equal(["step-done", "step-done", "step-done", "step-failed", "step-pending", "step-pending"], StepClasses(cut));
    }
}
