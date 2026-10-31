using Dashboard.Core.Recipes;

namespace Dashboard.Core.Tests;

public class CompatibilityPassTests
{
    private static RecipeModel PlainModel() => new() { Path = "/data/models/plain-model" };
    private static RecipeServer PlainServer() => new() { ModelsDir = "/data/models" };
    private static Recipe PlainRecipe() => new();

    [Fact]
    public void Check_TensorParallelSizeExceedingGpuCount_IsAHardBlock()
    {
        var recipe = PlainRecipe() with { ExtraArgs = new[] { new RecipeExtraArg("tensor-parallel-size", "4") } };
        var server = PlainServer() with { GpuCount = 1 };

        var result = CompatibilityPass.Check(PlainModel(), recipe, server);

        Assert.Contains(result.HardBlocks, i => i.Code == "tensor_parallel_exceeds_gpu_count");
    }

    [Fact]
    public void Check_TensorParallelSizeWithinGpuCount_IsNotBlocked()
    {
        var recipe = PlainRecipe() with { ExtraArgs = new[] { new RecipeExtraArg("tensor-parallel-size", "2") } };
        var server = PlainServer() with { GpuCount = 2 };

        var result = CompatibilityPass.Check(PlainModel(), recipe, server);

        Assert.Empty(result.HardBlocks);
    }

    [Fact]
    public void Check_EmptyModelPath_IsAHardBlock()
    {
        var model = PlainModel() with { Path = "" };

        var result = CompatibilityPass.Check(model, PlainRecipe(), PlainServer());

        Assert.Contains(result.HardBlocks, i => i.Code == "model_path_missing");
    }

    [Theory]
    [InlineData("downloading")]
    [InlineData("download_failed")]
    public void Check_ModelNotAvailable_IsAHardBlock(string status)
    {
        var model = PlainModel() with { Status = status };

        var result = CompatibilityPass.Check(model, PlainRecipe(), PlainServer());

        Assert.Contains(result.HardBlocks, i => i.Code == "model_not_available");
    }

    [Fact]
    public void Check_OversizedMaxModelLen_IsASoftWarning()
    {
        var model = PlainModel() with { ConfigMaxPositionEmbeddings = 8192 };
        var recipe = PlainRecipe() with { MaxModelLen = 131072 };

        var result = CompatibilityPass.Check(model, recipe, PlainServer());

        Assert.Empty(result.HardBlocks);
        Assert.Contains(result.SoftWarnings, i => i.Code == "max_model_len_exceeds_model_limit");
    }

    [Fact]
    public void Check_LowGpuMemoryUtilizationForModelSize_IsASoftWarning()
    {
        var model = PlainModel() with { SizeBytes = 20_000_000_000 }; // 20 GB of weights
        var server = PlainServer() with { GpuMemGb = 24 };
        var recipe = PlainRecipe() with { GpuMemoryUtilization = 0.5 }; // 12 GB budget -- too little

        var result = CompatibilityPass.Check(model, recipe, server);

        Assert.Contains(result.SoftWarnings, i => i.Code == "gpu_memory_utilization_too_low");
    }

    [Fact]
    public void Check_AmpleGpuMemoryUtilizationForModelSize_HasNoWarning()
    {
        var model = PlainModel() with { SizeBytes = 8_000_000_000 }; // 8 GB of weights
        var server = PlainServer() with { GpuMemGb = 24 };
        var recipe = PlainRecipe() with { GpuMemoryUtilization = 0.9 }; // ~21.6 GB budget -- plenty

        var result = CompatibilityPass.Check(model, recipe, server);

        Assert.DoesNotContain(result.SoftWarnings, i => i.Code == "gpu_memory_utilization_too_low");
    }
}
