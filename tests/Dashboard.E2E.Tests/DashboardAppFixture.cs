using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Dashboard.Infrastructure.Identity;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;
using Testcontainers.PostgreSql;

namespace Dashboard.E2E.Tests;

/// <summary>P13's E2E harness: launches the REAL Dashboard.Web app (the built
/// assembly, as a real OS process listening on a real socket -- Playwright
/// needs a real HTTP server, not an in-memory TestServer) against a throwaway
/// Testcontainers Postgres, and seeds one auto-created test user (local dev
/// host only; these credentials are test-only, never real ones -- doc's own
/// "E2E harness" requirement). One instance of the app + one browser are
/// shared by the whole "E2E" collection; each test gets its own page.</summary>
public sealed class DashboardAppFixture : IAsyncLifetime
{
    public const string TestUserEmail = "e2e-test-user@example.invalid";
    public const string TestUserPassword = "E2e-Test-Password-1!";

    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:16")
        .WithDatabase("dashboard_e2e")
        .WithUsername("dashboard")
        .WithPassword("dashboard")
        .Build();

    private readonly string _dataProtectionDir = Path.Combine(Path.GetTempPath(), "dashboard-e2e-dp-" + Guid.NewGuid().ToString("n"));
    private Process? _appProcess;
    private IPlaywright? _playwright;

    public string BaseUrl { get; private set; } = "";
    public IBrowser Browser { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        await _postgres.StartAsync();

        var port = GetFreeTcpPort();
        BaseUrl = $"http://127.0.0.1:{port}";

        var webDllPath = typeof(Program).Assembly.Location;
        var startInfo = new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = $"\"{webDllPath}\"",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        startInfo.EnvironmentVariables["ASPNETCORE_ENVIRONMENT"] = "Development";
        startInfo.EnvironmentVariables["ASPNETCORE_URLS"] = BaseUrl;
        startInfo.EnvironmentVariables["ConnectionStrings__DefaultConnection"] = _postgres.GetConnectionString();
        startInfo.EnvironmentVariables["DetailedErrors"] = "true";
        // Fixed, per-fixture key directory (P15b): the launched app's own
        // InstanceTokenProvider decrypts Instance.AdminTokenEnc with ASP.NET
        // Data Protection, so a test that wants to seed a working token must
        // encrypt it with a protector pointed at this SAME directory --
        // see EncryptTokenForApp.
        Directory.CreateDirectory(_dataProtectionDir);
        startInfo.EnvironmentVariables["DATAPROTECTION_KEYS_DIR"] = _dataProtectionDir;

        _appProcess = Process.Start(startInfo) ?? throw new InvalidOperationException("Failed to start Dashboard.Web.");
        // Drain stdout/stderr so the child never blocks on a full pipe buffer.
        _appProcess.OutputDataReceived += (_, _) => { };
        _appProcess.ErrorDataReceived += (_, _) => { };
        _appProcess.BeginOutputReadLine();
        _appProcess.BeginErrorReadLine();

        await WaitUntilServingAsync(TimeSpan.FromSeconds(60));
        await SeedTestUserAsync();

        Microsoft.Playwright.Program.Main(["install", "chromium"]);
        _playwright = await Playwright.CreateAsync();
        Browser = await _playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Args = new[] { "--no-sandbox" } });
    }

    public async Task DisposeAsync()
    {
        if (Browser is not null)
            await Browser.CloseAsync();
        _playwright?.Dispose();

        if (_appProcess is { HasExited: false })
        {
            try { _appProcess.Kill(entireProcessTree: true); } catch { /* best effort */ }
        }
        _appProcess?.Dispose();

        await _postgres.DisposeAsync();

        try { Directory.Delete(_dataProtectionDir, recursive: true); } catch { /* best effort */ }
    }

    /// <summary>Encrypts a token the same way the launched app's own
    /// InstanceTokenProvider will decrypt it: same key directory (this
    /// fixture's own, via DATAPROTECTION_KEYS_DIR), same application name,
    /// same purpose string. A test seeding Instance.AdminTokenEnc directly
    /// via NewDbContext() must go through this, not a dummy encrypted-looking
    /// string, or the running app will fail to decrypt it and silently treat
    /// the instance as having no token at all.</summary>
    public string EncryptTokenForApp(string token)
    {
        var provider = Microsoft.AspNetCore.DataProtection.DataProtectionProvider.Create(
            new DirectoryInfo(_dataProtectionDir),
            builder => builder.SetApplicationName("vllm-dashboard"));
        var protector = provider.CreateProtector(Dashboard.Infrastructure.Persistence.V2.InstanceTokenProvider.Purpose);
        return protector.Protect(token);
    }

    /// <summary>A short-lived <see cref="AppDbContext"/> against the same
    /// Testcontainers Postgres the running app uses, for test-side seeding/
    /// assertions that bypass the (not-yet-built) UI for those entities.</summary>
    public AppDbContext NewDbContext()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(_postgres.GetConnectionString())
            .Options;
        return new AppDbContext(options);
    }

    /// <summary>Logs in as the seeded test user in a fresh browser context and
    /// returns its page, positioned wherever ASP.NET Identity redirects after
    /// a successful login (the app's own "/").</summary>
    public async Task<IPage> NewLoggedInPageAsync()
    {
        var context = await Browser.NewContextAsync();
        var page = await context.NewPageAsync();
        await page.GotoAsync(BaseUrl + "/Account/Login", new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle });
        await page.FillAsync("input[name=\"Input.Email\"]", TestUserEmail);
        await page.FillAsync("input[name=\"Input.Password\"]", TestUserPassword);
        await page.ClickAsync("button[type=\"submit\"]");
        await page.WaitForLoadStateAsync(LoadState.NetworkIdle);
        return page;
    }

    private static int GetFreeTcpPort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private async Task WaitUntilServingAsync(TimeSpan timeout)
    {
        using var client = new HttpClient();
        var deadline = DateTime.UtcNow + timeout;
        Exception? last = null;
        while (DateTime.UtcNow < deadline)
        {
            if (_appProcess!.HasExited)
                throw new InvalidOperationException($"Dashboard.Web exited early with code {_appProcess.ExitCode}.");
            try
            {
                using var response = await client.GetAsync(BaseUrl + "/");
                return; // any response (even a 302 to login) means Kestrel is up
            }
            catch (Exception ex)
            {
                last = ex;
                await Task.Delay(500);
            }
        }
        throw new TimeoutException($"Dashboard.Web never started serving at {BaseUrl}.", last);
    }

    /// <summary>Mirrors Register.razor's own "first user becomes admin + active"
    /// bootstrap rule directly through Identity's services, rather than driving
    /// the Register page's antiforgery-protected HTML form -- same end state,
    /// far less brittle.</summary>
    private async Task SeedTestUserAsync()
    {
        var services = new ServiceCollection();
        services.AddDbContext<AppDbContext>(o => o.UseNpgsql(_postgres.GetConnectionString()));
        services.AddIdentityCore<ApplicationUser>(o => o.Password.RequiredLength = 8)
            .AddRoles<IdentityRole>()
            .AddEntityFrameworkStores<AppDbContext>();
        var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var roleManager = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole>>();

        if (await roleManager.FindByNameAsync("admin") is null)
            await roleManager.CreateAsync(new IdentityRole("admin"));

        var existing = await userManager.FindByEmailAsync(TestUserEmail);
        if (existing is not null)
            return;

        var user = new ApplicationUser
        {
            UserName = TestUserEmail, Email = TestUserEmail, EmailConfirmed = true, Status = UserStatus.Active,
        };
        var result = await userManager.CreateAsync(user, TestUserPassword);
        if (!result.Succeeded)
            throw new InvalidOperationException("Could not seed the E2E test user: " + string.Join("; ", result.Errors.Select(e => e.Description)));
        await userManager.AddToRoleAsync(user, "admin");
    }
}

[CollectionDefinition("E2E")]
public sealed class DashboardE2ECollection : ICollectionFixture<DashboardAppFixture>;
