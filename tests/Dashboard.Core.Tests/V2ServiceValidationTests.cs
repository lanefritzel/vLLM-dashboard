using Dashboard.Infrastructure.Persistence.V2;
using Microsoft.Extensions.DependencyInjection;

namespace Dashboard.Core.Tests;

/// <summary>Unit (incident-reproduction) coverage for the blank-field save-time
/// guardrails (P09b) -- the literal first qwen incident was an empty --model
/// crash-looping the container; these are the equivalent guardrails for each
/// entity's own identifying field. Each service validates before ever touching
/// the DB, so passing a scope factory that would blow up if used (never
/// reached) keeps these true unit tests, no Postgres needed.</summary>
public class V2ServiceValidationTests
{
    private static IServiceScopeFactory UnusedScopeFactory => null!;

    [Fact]
    public async Task ServerService_CreateWithBlankLabel_Rejected()
    {
        var service = new ServerService(UnusedScopeFactory);
        var server = new Server { Label = "", ModelsDir = "/models" };

        await Assert.ThrowsAsync<ArgumentException>(() => service.CreateAsync(server));
    }

    [Fact]
    public async Task ServerService_CreateWithBlankModelsDir_Rejected()
    {
        var service = new ServerService(UnusedScopeFactory);
        var server = new Server { Label = "primary", ModelsDir = "" };

        await Assert.ThrowsAsync<ArgumentException>(() => service.CreateAsync(server));
    }

    [Fact]
    public async Task ModelService_CreateWithBlankPath_Rejected()
    {
        // The literal first qwen incident: an empty --model crash-looped the
        // container. A model with no weights location can never be deployed.
        var service = new ModelService(UnusedScopeFactory);
        var model = new Model { ServerId = 1, Name = "qwen", Path = "" };

        await Assert.ThrowsAsync<ArgumentException>(() => service.CreateAsync(model));
    }

    [Fact]
    public async Task ModelService_CreateWithBlankName_Rejected()
    {
        var service = new ModelService(UnusedScopeFactory);
        var model = new Model { ServerId = 1, Name = "", Path = "/models/hf-cache/qwen" };

        await Assert.ThrowsAsync<ArgumentException>(() => service.CreateAsync(model));
    }

    [Fact]
    public async Task InstanceService_CreateWithBlankLabel_Rejected()
    {
        var service = new InstanceService(UnusedScopeFactory);
        var instance = new Instance { ServerId = 1, Label = "", ProxySlug = "main" };

        await Assert.ThrowsAsync<ArgumentException>(() => service.CreateAsync(instance));
    }

    [Theory]
    [InlineData("")]
    [InlineData("Has Spaces")]
    [InlineData("UPPERCASE")]
    [InlineData("-leading-hyphen")]
    [InlineData("trailing-hyphen-")]
    public async Task InstanceService_CreateWithNonUrlSafeSlug_Rejected(string slug)
    {
        var service = new InstanceService(UnusedScopeFactory);
        var instance = new Instance { ServerId = 1, Label = "main vLLM", ProxySlug = slug };

        await Assert.ThrowsAsync<ArgumentException>(() => service.CreateAsync(instance));
    }

    [Fact]
    public async Task InstanceService_CreateWithUrlSafeSlug_DoesNotRejectOnValidation()
    {
        // Validation passes -- it'll then try to actually hit the DB via the
        // null scope factory, which is the expected failure past this point;
        // asserting it's NOT an ArgumentException proves validation itself let
        // a well-formed slug through.
        var service = new InstanceService(UnusedScopeFactory);
        var instance = new Instance { ServerId = 1, Label = "main vLLM", ProxySlug = "main-vllm-2" };

        var ex = await Record.ExceptionAsync(() => service.CreateAsync(instance));
        Assert.IsNotType<ArgumentException>(ex);
    }
}
