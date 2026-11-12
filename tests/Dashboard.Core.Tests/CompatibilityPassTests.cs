using Dashboard.Core.Recipes;

namespace Dashboard.Core.Tests;

public class CompatibilityPassTests
{
    private static RecipeModel PlainModel() => new() { Path = "/data/models/hf-cache/plain-model" };
    private static RecipeServer PlainServer() => new() { ModelsDir = "/data/models" };
    private static VllmRecipe PlainRecipe() => new();

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

    // --- P09b: dtype rule (v1, owner-confirmed 2026-11-01 -- the second qwen incident) ---

    [Fact]
    public void Check_DtypeBfloat16OnIntelServer_IsAHardBlock()
    {
        var server = PlainServer() with { GpuName = "Intel Arc A770" };
        var recipe = PlainRecipe() with { Dtype = "bfloat16" };

        var result = CompatibilityPass.Check(PlainModel(), recipe, server);

        Assert.Contains(result.HardBlocks, i => i.Code == "dtype_bfloat16_blocked_on_intel");
    }

    [Theory]
    [InlineData("auto")]
    [InlineData("")]
    public void Check_DtypeAutoOrUnsetOnIntelServer_IsASoftWarningNotAHardBlock(string dtype)
    {
        var server = PlainServer() with { GpuName = "Intel Arc A770" };
        var recipe = PlainRecipe() with { Dtype = dtype };

        var result = CompatibilityPass.Check(PlainModel(), recipe, server);

        Assert.Empty(result.HardBlocks);
        Assert.Contains(result.SoftWarnings, i => i.Code == "dtype_auto_on_intel");
    }

    [Fact]
    public void Check_DtypeFloat16OnIntelServer_Passes()
    {
        var server = PlainServer() with { GpuName = "Intel Arc A770" };
        var recipe = PlainRecipe() with { Dtype = "float16" };

        var result = CompatibilityPass.Check(PlainModel(), recipe, server);

        Assert.Empty(result.HardBlocks);
        Assert.DoesNotContain(result.SoftWarnings, i => i.Code == "dtype_auto_on_intel");
    }

    [Fact]
    public void Check_DtypeBfloat16OnNonIntelServer_HasNoDtypeIssue()
    {
        var server = PlainServer() with { GpuName = "NVIDIA RTX 4090" };
        var recipe = PlainRecipe() with { Dtype = "bfloat16" };

        var result = CompatibilityPass.Check(PlainModel(), recipe, server);

        Assert.DoesNotContain(result.HardBlocks, i => i.Code == "dtype_bfloat16_blocked_on_intel");
        Assert.DoesNotContain(result.SoftWarnings, i => i.Code == "dtype_auto_on_intel");
    }

    [Fact]
    public void Check_DtypeBfloat16WithNoGpuNameAtAll_HasNoDtypeIssue()
    {
        var server = PlainServer() with { GpuName = null };
        var recipe = PlainRecipe() with { Dtype = "bfloat16" };

        var result = CompatibilityPass.Check(PlainModel(), recipe, server);

        Assert.DoesNotContain(result.HardBlocks, i => i.Code == "dtype_bfloat16_blocked_on_intel");
    }

    // --- P09b: field rules ---

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Check_NonPositiveMaxModelLen_IsAHardBlock(int maxModelLen)
    {
        var recipe = PlainRecipe() with { MaxModelLen = maxModelLen };

        var result = CompatibilityPass.Check(PlainModel(), recipe, PlainServer());

        Assert.Contains(result.HardBlocks, i => i.Code == "max_model_len_not_positive");
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(-0.1)]
    [InlineData(1.1)]
    public void Check_GpuMemoryUtilizationOutOfRange_IsAHardBlock(double value)
    {
        var recipe = PlainRecipe() with { GpuMemoryUtilization = value };

        var result = CompatibilityPass.Check(PlainModel(), recipe, PlainServer());

        Assert.Contains(result.HardBlocks, i => i.Code == "gpu_memory_utilization_out_of_range");
    }

    [Fact]
    public void Check_GpuMemoryUtilizationExactlyOne_Passes()
    {
        var recipe = PlainRecipe() with { GpuMemoryUtilization = 1.0 };

        var result = CompatibilityPass.Check(PlainModel(), recipe, PlainServer());

        Assert.DoesNotContain(result.HardBlocks, i => i.Code == "gpu_memory_utilization_out_of_range");
    }

    [Theory]
    [InlineData("")]
    [InlineData("has space")]
    [InlineData("-leading-dash")]
    public void Check_InvalidServedModelName_IsAHardBlock(string servedModelName)
    {
        var recipe = PlainRecipe() with { ServedModelName = servedModelName };

        var result = CompatibilityPass.Check(PlainModel(), recipe, PlainServer());

        Assert.Contains(result.HardBlocks, i => i.Code == "served_model_name_invalid");
    }

    [Theory]
    [InlineData("main")]
    [InlineData("qwen3-8b")]
    [InlineData("model.v1_2")]
    public void Check_ValidServedModelName_Passes(string servedModelName)
    {
        var recipe = PlainRecipe() with { ServedModelName = servedModelName };

        var result = CompatibilityPass.Check(PlainModel(), recipe, PlainServer());

        Assert.DoesNotContain(result.HardBlocks, i => i.Code == "served_model_name_invalid");
    }

    [Fact]
    public void Check_ModelPathOutsideServersModelsDir_IsAHardBlock()
    {
        var model = PlainModel() with { Path = "/somewhere/else/some-model" };

        var result = CompatibilityPass.Check(model, PlainRecipe(), PlainServer());

        Assert.Contains(result.HardBlocks, i => i.Code == "model_path_outside_models_dir");
    }

    [Fact]
    public void Check_ModelPathUnderServersHfCache_HasNoPathIssue()
    {
        var model = PlainModel() with { Path = "/data/models/hf-cache/some-model/model.safetensors" };

        var result = CompatibilityPass.Check(model, PlainRecipe(), PlainServer());

        Assert.DoesNotContain(result.HardBlocks, i => i.Code == "model_path_outside_models_dir");
    }

    [Fact]
    public void Check_ModelPathUnderModelsDirButNotHfCache_HasNoPathIssue()
    {
        // The dashboard's own HF downloads default to models_dir/hf-cache/<slug>,
        // but that's no longer a requirement for every model -- a model placed
        // anywhere else under models_dir (manually registered, a different
        // existing layout, etc.) is equally valid.
        var model = PlainModel() with { Path = "/data/models/not-hf-cache/some-model" };

        var result = CompatibilityPass.Check(model, PlainRecipe(), PlainServer());

        Assert.DoesNotContain(result.HardBlocks, i => i.Code == "model_path_outside_models_dir");
    }
}
