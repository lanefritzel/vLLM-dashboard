using Dashboard.Infrastructure.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;

namespace Dashboard.Web.Middleware;

public static class PendingUserGuard
{
    public static IApplicationBuilder UsePendingUserGuard(this IApplicationBuilder app)
    {
        return app.Use(async (context, next) =>
        {
            var path = context.Request.Path.Value ?? "";
            if (context.User.Identity?.IsAuthenticated == true && !IsAllowedAnonymousPath(path))
            {
                var scopeFactory = context.RequestServices.GetRequiredService<IServiceScopeFactory>();
                using var scope = scopeFactory.CreateScope();
                var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
                var user = await userManager.GetUserAsync(context.User);
                if (user is not null && user.Status != UserStatus.Active)
                {
                    context.Response.Redirect("/awaiting-approval");
                    return;
                }
            }

            await next();
        });
    }

    private static bool IsAllowedAnonymousPath(string path)
    {
        var allowed = new[] { "/awaiting-approval", "/Account/", "/Identity/", "/Error", "/not-found", "/lib/", "/favicon.png" };
        return allowed.Any(p => path.StartsWith(p, StringComparison.OrdinalIgnoreCase));
    }
}
