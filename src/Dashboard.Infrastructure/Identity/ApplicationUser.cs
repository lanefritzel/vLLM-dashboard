using Microsoft.AspNetCore.Identity;

namespace Dashboard.Infrastructure.Identity;

public class ApplicationUser : IdentityUser
{
    public string Status { get; set; } = UserStatus.Pending;
    public string ThemeMode { get; set; } = "dark";
}

public static class UserStatus
{
    public const string Active = "active";
    public const string Pending = "pending";
}
