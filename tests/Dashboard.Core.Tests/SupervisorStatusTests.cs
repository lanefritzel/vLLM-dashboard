using System.Text.Json;
using Dashboard.Core.Supervisor;

namespace Dashboard.Core.Tests;

/// <summary>P2: GET /admin/status gained an "engine" block; the DTO must read it
/// with the same snake_case policy SupervisorClient uses, and tolerate an older
/// supervisor that doesn't send it.</summary>
public class SupervisorStatusTests
{
    private static readonly JsonSerializerOptions Options = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    [Fact]
    public void Engine_DeserializesNameAndVersion()
    {
        var json = """{"state":"ready","vllm_healthy":true,"engine":{"name":"llamacpp","version":"b12242"}}""";
        var status = JsonSerializer.Deserialize<SupervisorStatus>(json, Options)!;
        Assert.Equal("llamacpp", status.Engine!.Name);
        Assert.Equal("b12242", status.Engine.Version);
    }

    [Fact]
    public void Engine_NullVersionIsAllowed()
    {
        var json = """{"state":"idle","engine":{"name":"vllm","version":null}}""";
        var status = JsonSerializer.Deserialize<SupervisorStatus>(json, Options)!;
        Assert.Equal("vllm", status.Engine!.Name);
        Assert.Null(status.Engine.Version);
    }

    [Fact]
    public void Engine_AbsentFromAnOlderSupervisor_IsNull()
    {
        var status = JsonSerializer.Deserialize<SupervisorStatus>("""{"state":"idle"}""", Options)!;
        Assert.Null(status.Engine);
    }
}
