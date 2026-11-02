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

    /// <summary><paramref name="extraEnv"/> (P14) lets a caller select e.g.
    /// <c>GPU_BACKEND=fake</c> + <c>GPU_BACKEND_FAKE_PATH</c> to control what
    /// <c>/admin/gpu</c> reports -- used by the Stop/VRAM-release E2E tests,
    /// which need to simulate VRAM staying held after Stop without a real GPU.
    /// <paramref name="inferencePort"/> (P15a) additionally maps the fake
    /// vLLM child's own port out of the container -- host and container port
    /// must be the SAME number (the dashboard, running on the host, connects
    /// using this literal number, while deploy_runner.py's "--port" flag,
    /// built from this same Instance.Port value, tells the child what to bind
    /// to inside the container). Deliberately caller-chosen, not a fixed
    /// constant: a fixed port collided ("already allocated") when a previous
    /// test's container hadn't fully released it by the time the next one
    /// started -- pick a fresh ephemeral port per test instead (see
    /// DashboardAppFixture.GetFreeTcpPort for the same pattern).</summary>
    public async Task<IContainer> StartContainerAsync(IReadOnlyDictionary<string, string>? extraEnv = null, int? inferencePort = null)
    {
        var builder = new ContainerBuilder(_image!)
            .WithPortBinding(AdminPort, true)
            .WithEnvironment("SUPERVISOR_TOKEN", Token)
            .WithEnvironment("ADMIN_PORT", AdminPort.ToString())
            .WithEnvironment("AUTO_START", "off")
            .WithWaitStrategy(Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(r => r.ForPort(AdminPort).ForPath("/admin/health")));
        if (inferencePort is { } port)
            builder = builder.WithPortBinding(port, port);
        if (extraEnv is not null)
        {
            foreach (var (key, value) in extraEnv)
                builder = builder.WithEnvironment(key, value);
        }
        var container = builder.Build();
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
