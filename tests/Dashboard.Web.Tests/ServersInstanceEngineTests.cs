using Bunit;
using Dashboard.Core.Abstractions;
using Dashboard.Core.Models;
using Dashboard.Infrastructure.Persistence.V2;
using Dashboard.Web.Components.Pages;
using Dashboard.Web.Components.Shared;
using Microsoft.Extensions.DependencyInjection;

namespace Dashboard.Web.Tests;

/// <summary>P3 review fixes: the Servers page's instance form must carry an instance's engine
/// through an edit (it used to rebuild the Instance from scratch, silently resetting it to vLLM)
/// and must let the operator choose one when adding an instance.</summary>
public class ServersInstanceEngineTests : BunitContext
{
    private readonly List<Instance> _updated = new();
    private readonly List<Instance> _created = new();

    private IRenderedComponent<Servers> RenderPage(params Instance[] existing)
    {
        Services.AddSingleton(ProxyStub.Create<IServerService>(h =>
            h["ListAsync"] = _ => Task.FromResult<IReadOnlyList<Server>>([new Server { Id = 1, Label = "s", ModelsDir = "/models" }])));
        Services.AddSingleton(ProxyStub.Create<IInstanceService>(h =>
        {
            h["ListByServerAsync"] = _ => Task.FromResult<IReadOnlyList<Instance>>(existing);
            h["UpdateAsync"] = a => { var i = (Instance)a[0]!; _updated.Add(i); return Task.FromResult((i, false)); };
            h["CreateAsync"] = a => { var i = (Instance)a[0]!; _created.Add(i); return Task.FromResult(i); };
        }));
        Services.AddSingleton(ProxyStub.Create<ISupervisorClient>());
        Services.AddSingleton(ProxyStub.Create<IDialogService>());
        JSInterop.Mode = JSRuntimeMode.Loose;
        return Render<Servers>();
    }

    private static Instance Llama() => new()
    {
        Id = 3, ServerId = 1, Label = "llama", ProxySlug = "llama", Port = 8080, Engine = "llamacpp",
    };

    [Fact]
    public void EditingAnInstance_PreselectsItsEngine()
    {
        var cut = RenderPage(Llama());
        cut.FindAll("button").Last(b => b.TextContent.Trim() == "Edit").Click();
        Assert.Equal("llamacpp", cut.Find("#in-engine").GetAttribute("value"));
    }

    [Fact]
    public void SavingAnEditedLlamaCppInstance_KeepsItsEngine()
    {
        var cut = RenderPage(Llama());
        cut.FindAll("button").Last(b => b.TextContent.Trim() == "Edit").Click();
        cut.Find("#in-label").Input("renamed");
        cut.Find("form#instance-form").Submit();

        var saved = Assert.Single(_updated);
        Assert.Equal("renamed", saved.Label);
        Assert.Equal("llamacpp", saved.Engine);
    }

    [Fact]
    public void AddingAnInstance_LetsTheOperatorPickLlamaCpp()
    {
        var cut = RenderPage();
        cut.FindAll("button").First(b => b.TextContent.Contains("Add instance")).Click();
        cut.Find("#in-label").Input("new");
        cut.Find("#in-slug").Input("new");
        cut.Find("#in-engine").Change("llamacpp");
        cut.Find("form#instance-form").Submit();

        Assert.Equal("llamacpp", Assert.Single(_created).Engine);
    }

    [Fact]
    public void AddingAnInstance_DefaultsToVllm()
    {
        var cut = RenderPage();
        cut.FindAll("button").First(b => b.TextContent.Contains("Add instance")).Click();
        cut.Find("#in-label").Input("new");
        cut.Find("#in-slug").Input("new");
        cut.Find("form#instance-form").Submit();

        Assert.Equal("vllm", Assert.Single(_created).Engine);
    }

    [Fact]
    public void EngineDropdown_OffersExactlyTheEnginesCoreDefines_AndNothingTypeable()
    {
        var cut = RenderPage();
        cut.FindAll("button").First(b => b.TextContent.Contains("Add instance")).Click();

        var select = cut.Find("select#in-engine");
        var offered = select.QuerySelectorAll("option").Select(o => (o.GetAttribute("value"), o.TextContent.Trim())).ToArray();
        Assert.Equal(EngineCapabilities.Options.Select(o => (o.Storage, o.Label)), offered);
        Assert.Empty(cut.FindAll("input#in-engine")); // a dropdown, never a free-text input
    }
}
