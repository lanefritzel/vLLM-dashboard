using Dashboard.Core.Downloads;

namespace Dashboard.Core.Tests;

public class DiskGuardTests
{
    [Fact]
    public void Check_AvailableAboveRequired_IsOk()
    {
        var result = DiskGuard.Check(requiredBytes: 1000, availableBytes: 2000);

        Assert.True(result.Ok);
        Assert.Null(result.Message);
    }

    [Fact]
    public void Check_AvailableEqualToRequired_IsOk()
    {
        var result = DiskGuard.Check(requiredBytes: 1000, availableBytes: 1000);

        Assert.True(result.Ok);
    }

    [Fact]
    public void Check_AvailableBelowRequired_IsBlockedWithRequiredAndAvailableInMessage()
    {
        var result = DiskGuard.Check(requiredBytes: 5_000_000_000, availableBytes: 1_000_000_000);

        Assert.False(result.Ok);
        Assert.NotNull(result.Message);
        Assert.Contains("5,000,000,000", result.Message);
        Assert.Contains("1,000,000,000", result.Message);
    }

    [Fact]
    public void Check_ZeroRequired_IsAlwaysOk()
    {
        var result = DiskGuard.Check(requiredBytes: 0, availableBytes: 0);

        Assert.True(result.Ok);
    }
}
