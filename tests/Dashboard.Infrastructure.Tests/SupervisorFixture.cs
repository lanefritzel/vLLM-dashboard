using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using DotNet.Testcontainers.Images;

namespace Dashboard.Infrastructure.Tests;

/// <summary>Builds the REAL supervisor test image (supervisor/Dockerfile.test,
/// the same one used for every phase's own manual Docker verification since
/// P02) ONCE per test run (P10a's own work item: "dashboard service + real
/// supervisor container + fake vLLM" -- not a C# stand-in for the admin API).
/// Each test then starts its OWN fresh container from this already-built
/// image via <see cref="StartContainerAsync"/> -- a shared container would
/// leak state between tests (its deploy_runner's in-memory event store
/// persists across calls, and deployment ids would collide once each test's
/// fresh Postgres DB starts counting from 1 again).</summary>
public sealed class SupervisorImageFixture : IAsyncLifetime
{
    public const string Token = "test-token";
    public const int AdminPort = 8080;

    private IFutureDockerImage? _image;

    public async Task InitializeAsync()
    {
        var repoRoot = FindRepoRoot();
        _image = new ImageFromDockerfileBuilder()
            .WithDockerfileDirectory(repoRoot)
            .WithDockerfile("supervisor/Dockerfile.test")
            .Build();
        await _image.CreateAsync();
    }

    public async Task DisposeAsync()
    {
        if (_image is not null)
            await _image.DisposeAsync();
    }

    public async Task<IContainer> StartContainerAsync()
    {
        var container = new ContainerBuilder(_image!)
            .WithPortBinding(AdminPort, true)
            .WithEnvironment("SUPERVISOR_TOKEN", Token)
            .WithEnvironment("ADMIN_PORT", AdminPort.ToString())
            .WithEnvironment("AUTO_START", "off")
            .WithWaitStrategy(Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(r => r.ForPort(AdminPort).ForPath("/admin/health")))
            .Build();
        await container.StartAsync();
        return container;
    }

    public static string AdminUrl(IContainer container) => $"http://127.0.0.1:{container.GetMappedPublicPort(AdminPort)}";

    /// <summary>Walks up from the test assembly's own output directory to the
    /// repo root (marked by .git) -- there's no .sln file to anchor on
    /// (Testcontainers' own CommonDirectoryPath.GetSolutionDirectory() needs
    /// one), and the Dockerfile's build context must be the repo root (it
    /// also copies tests/fake-vllm/fake_vllm.py, outside supervisor/).</summary>
    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, ".git")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException($"Could not find repo root (.git) from {AppContext.BaseDirectory}.");
    }
}

/// <summary>SwapServiceTests needs both a fresh-per-test Postgres database AND
/// the supervisor image built once -- a combined collection lets one test
/// class pull in both fixtures (xUnit only allows one [Collection] per
/// class). Deliberately separate from the existing "Postgres" collection
/// (used by every other Infrastructure.Tests file) rather than reusing it,
/// so this file's own Postgres container's lifetime is independent.</summary>
[CollectionDefinition("PostgresAndSupervisor")]
public sealed class PostgresAndSupervisorCollection : ICollectionFixture<PostgresFixture>, ICollectionFixture<SupervisorImageFixture>;
