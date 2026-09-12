using Dashboard.Core.Models;

namespace Dashboard.Core.Tests;

public class ModelInfoTests
{
    private static ModelInfo BaseModel() => new()
    {
        Id = "m1",
        PhysicalServerId = "s1",
        Name = "qwen3.8-vllm",
        DisplayName = "Qwen 3.8",
        ModelSource = ModelSource.LocalPath,
        ModelPathOrRepoId = "/models/qwen3-8-vllm",
        ImageOverride = null,
        Options = new Dictionary<string, string> { ["dtype"] = "bfloat16" },
        SortOrder = 0,
        IdleTtlSeconds = 600,
    };

    [Fact]
    public void RequiresReload_FalseWhenNothingChanged()
    {
        var before = BaseModel();
        var after = BaseModel();
        Assert.False(ModelInfo.RequiresReload(before, after));
    }

    [Theory]
    [InlineData(nameof(ModelInfo.DisplayName))]
    [InlineData(nameof(ModelInfo.SortOrder))]
    [InlineData(nameof(ModelInfo.IdleTtlSeconds))]
    [InlineData(nameof(ModelInfo.PhysicalServerId))]
    public void RequiresReload_FalseForBookkeepingOnlyFields(string field)
    {
        var before = BaseModel();
        var after = field switch
        {
            nameof(ModelInfo.DisplayName) => before with { DisplayName = "Something else" },
            nameof(ModelInfo.SortOrder) => before with { SortOrder = 5 },
            nameof(ModelInfo.IdleTtlSeconds) => before with { IdleTtlSeconds = 1200 },
            nameof(ModelInfo.PhysicalServerId) => before with { PhysicalServerId = "s2" },
            _ => throw new ArgumentOutOfRangeException(nameof(field)),
        };
        Assert.False(ModelInfo.RequiresReload(before, after));
    }

    [Fact]
    public void RequiresReload_TrueWhenNameChanges()
    {
        var before = BaseModel();
        var after = before with { Name = "qwen3.8-renamed" };
        Assert.True(ModelInfo.RequiresReload(before, after));
    }

    [Fact]
    public void RequiresReload_TrueWhenModelSourceChanges()
    {
        var before = BaseModel();
        var after = before with { ModelSource = ModelSource.HuggingFace };
        Assert.True(ModelInfo.RequiresReload(before, after));
    }

    [Fact]
    public void RequiresReload_TrueWhenModelPathOrRepoIdChanges()
    {
        var before = BaseModel();
        var after = before with { ModelPathOrRepoId = "/models/different-path" };
        Assert.True(ModelInfo.RequiresReload(before, after));
    }

    [Fact]
    public void RequiresReload_TrueWhenImageOverrideChanges()
    {
        var before = BaseModel();
        var after = before with { ImageOverride = "custom/image:latest" };
        Assert.True(ModelInfo.RequiresReload(before, after));
    }

    [Fact]
    public void RequiresReload_TrueWhenOptionValueChanges()
    {
        var before = BaseModel();
        var after = before with { Options = new Dictionary<string, string> { ["dtype"] = "float16" } };
        Assert.True(ModelInfo.RequiresReload(before, after));
    }

    [Fact]
    public void RequiresReload_TrueWhenOptionAdded()
    {
        var before = BaseModel();
        var after = before with
        {
            Options = new Dictionary<string, string>(before.Options) { ["enable-auto-tool-choice"] = "true" },
        };
        Assert.True(ModelInfo.RequiresReload(before, after));
    }

    [Fact]
    public void RequiresReload_TrueWhenOptionRemoved()
    {
        var before = BaseModel();
        var after = before with { Options = new Dictionary<string, string>() };
        Assert.True(ModelInfo.RequiresReload(before, after));
    }
}
