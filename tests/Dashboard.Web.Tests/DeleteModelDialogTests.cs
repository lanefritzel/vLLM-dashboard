using Bunit;
using Dashboard.Core.Abstractions;
using Dashboard.Core.Supervisor;
using Dashboard.Infrastructure.Persistence.V2;
using Dashboard.Web.Components.ModelsRecipes;
using Microsoft.Extensions.DependencyInjection;

namespace Dashboard.Web.Tests;

public class DeleteModelDialogTests : BunitContext
{
    private static Model TestModel() => new()
    {
        Id = 7, ServerId = 1, Name = "gemma", Path = "/models/gemma", SizeBytes = 1024 * 1024 * 1024,
        SourceType = "local", Status = "available",
    };

    private static Instance TestInstance(string? adminUrl = "http://host:8000/admin") => new()
    {
        Id = 1, ServerId = 1, Label = "i", ProxySlug = "i", AdminUrl = adminUrl,
    };

    private (IRenderedComponent<DeleteModelDialog> Cut, List<(int Id, bool DeleteFiles)> Deletes, List<bool> Deleted, List<bool> Cancelled)
        Render(Model model, Instance? instance, SupervisorStatus? status = null, Exception? statusThrows = null, Exception? deleteThrows = null)
    {
        var deletes = new List<(int, bool)>();
        Services.AddSingleton(ProxyStub.Create<IModelService>(h => h["DeleteAsync"] = args =>
        {
            var id = (int)args[0]!;
            var deleteFiles = (bool)args[1]!;
            deletes.Add((id, deleteFiles));
            if (deleteThrows is not null) throw deleteThrows;
            return Task.CompletedTask;
        }));
        Services.AddSingleton(ProxyStub.Create<ISupervisorClient>(h => h["GetStatusAsync"] = args =>
        {
            if (statusThrows is not null) throw statusThrows;
            return Task.FromResult(status ?? new SupervisorStatus { State = "idle", Capabilities = ["model_delete"] });
        }));
        JSInterop.Mode = JSRuntimeMode.Loose;

        var deleted = new List<bool>();
        var cancelled = new List<bool>();
        var cut = Render<DeleteModelDialog>(p => p
            .Add(c => c.Model, model)
            .Add(c => c.Instance, instance)
            .Add(c => c.OnDeleted, () => deleted.Add(true))
            .Add(c => c.OnCancelled, () => cancelled.Add(true)));
        return (cut, deletes, deleted, cancelled);
    }

    [Fact]
    public void Toggle_IsEnabled_WhenTheSupervisorReportsModelDeleteCapability()
    {
        var (cut, _, _, _) = Render(TestModel(), TestInstance());

        var toggle = cut.Find("input.ui-toggle-input");
        Assert.False(toggle.HasAttribute("disabled"));
    }

    [Fact]
    public void Toggle_IsDisabled_WithAnOlderSupervisor_AndExplainsWhy()
    {
        var (cut, _, _, _) = Render(TestModel(), TestInstance(), status: new SupervisorStatus { State = "idle", Capabilities = [] });

        var toggle = cut.Find("input.ui-toggle-input");
        Assert.True(toggle.HasAttribute("disabled"));
        Assert.Contains("Requires a newer supervisor", cut.Markup);
    }

    [Fact]
    public void Toggle_IsDisabled_WhenNoInstanceIsRegistered_AndExplainsWhy()
    {
        var (cut, _, _, _) = Render(TestModel(), instance: null);

        var toggle = cut.Find("input.ui-toggle-input");
        Assert.True(toggle.HasAttribute("disabled"));
        Assert.Contains("Can't reach the supervisor", cut.Markup);
    }

    [Fact]
    public void Toggle_IsDisabled_WhenTheSupervisorIsUnreachable_AndExplainsWhy()
    {
        var (cut, _, _, _) = Render(TestModel(), TestInstance(), statusThrows: new HttpRequestException("connection refused"));

        var toggle = cut.Find("input.ui-toggle-input");
        Assert.True(toggle.HasAttribute("disabled"));
        Assert.Contains("Can't reach the supervisor", cut.Markup);
    }

    [Fact]
    public void Delete_WithTheToggleOff_DeletesWithoutFiles()
    {
        var (cut, deletes, deleted, _) = Render(TestModel(), TestInstance());

        cut.FindAll("button").First(b => b.TextContent.Trim() == "Delete").Click();

        var sent = Assert.Single(deletes);
        Assert.Equal(7, sent.Id);
        Assert.False(sent.DeleteFiles);
        Assert.Single(deleted);
    }

    [Fact]
    public void Delete_WithTheToggleOn_DeletesWithFiles()
    {
        var (cut, deletes, deleted, _) = Render(TestModel(), TestInstance());

        cut.Find("input.ui-toggle-input").Change(true);
        cut.FindAll("button").First(b => b.TextContent.Trim() == "Delete").Click();

        var sent = Assert.Single(deletes);
        Assert.True(sent.DeleteFiles);
        Assert.Single(deleted);
    }

    [Fact]
    public void Delete_ShowsTheError_AndDoesNotReportDeleted_WhenTheServiceThrows()
    {
        var (cut, _, deleted, _) = Render(TestModel(), TestInstance(), deleteThrows: new InvalidOperationException("no reachable supervisor is registered for this server."));

        cut.FindAll("button").First(b => b.TextContent.Trim() == "Delete").Click();

        Assert.Contains("Delete failed: no reachable supervisor is registered for this server.", cut.Markup);
        Assert.Empty(deleted);
    }

    [Fact]
    public void Cancel_ReportsCancelled_WithoutDeleting()
    {
        var (cut, deletes, _, cancelled) = Render(TestModel(), TestInstance());

        cut.FindAll("button").First(b => b.TextContent.Trim() == "Cancel").Click();

        Assert.Single(cancelled);
        Assert.Empty(deletes);
    }
}
