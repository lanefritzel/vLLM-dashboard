using Dashboard.Infrastructure.Identity;

namespace Dashboard.Core.Tests;

public class IdentityModelTests
{
    [Fact]
    public void UserStatus_DefaultsToPending()
    {
        var user = new ApplicationUser();
        Assert.Equal(UserStatus.Pending, user.Status);
    }

    [Fact]
    public void UserStatus_HasActiveAndPending()
    {
        Assert.Equal("active", UserStatus.Active);
        Assert.Equal("pending", UserStatus.Pending);
    }
}
