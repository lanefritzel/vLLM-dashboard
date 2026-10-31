using System.Text.Json;
using Dashboard.Infrastructure.Identity;
using Dashboard.Infrastructure.Persistence;

namespace Dashboard.Infrastructure.Tests;

/// <summary>Inserts a <see cref="LegacyOracleFixture"/>'s rows into the legacy
/// (pre-V2) tables of an already-migrated AppDbContext, mirroring exactly what a
/// real upgrade starts from -- P01b's conversion routine runs against this.</summary>
public static class LegacyOracleFixtureSeeder
{
    public static async Task SeedAsync(AppDbContext db, LegacyOracleFixture fixture)
    {
        PhysicalServer? physicalServer = null;
        if (fixture.PhysicalServer is { } fixturePhysicalServer)
        {
            physicalServer = new PhysicalServer
            {
                Label = fixturePhysicalServer.Label,
                DockerHost = fixturePhysicalServer.DockerHost,
                GpuVendor = fixturePhysicalServer.GpuVendor,
                GpuVramTotalGb = fixturePhysicalServer.GpuVramTotalGb,
                HfCacheHostPath = fixturePhysicalServer.HfCacheHostPath,
                HfToken = fixturePhysicalServer.HfToken,
            };
            db.PhysicalServers.Add(physicalServer);
        }

        foreach (var fixtureSource in fixture.Sources)
        {
            var source = new Source
            {
                Slug = fixtureSource.Slug,
                Label = fixtureSource.Label,
                Type = fixtureSource.Type,
                MetricsUrl = fixtureSource.MetricsUrl,
                InferenceUrl = fixtureSource.InferenceUrl,
                DockerHost = fixtureSource.DockerHost,
                PhysicalServerId = physicalServer?.Id,
                Active = fixtureSource.Active,
            };
            db.Sources.Add(source);

            if (fixtureSource.LaunchSpec is { } fixtureLaunchSpec)
            {
                db.LaunchSpecs.Add(new LaunchSpecEntity
                {
                    SourceId = source.Id,
                    Image = fixtureLaunchSpec.Image,
                    RestartPolicy = fixtureLaunchSpec.RestartPolicy,
                    ServingPort = fixtureLaunchSpec.ServingPort,
                    NetworkMode = fixtureLaunchSpec.NetworkMode,
                    IpcMode = fixtureLaunchSpec.IpcMode,
                    Privileged = fixtureLaunchSpec.Privileged,
                    Entrypoint = fixtureLaunchSpec.Entrypoint,
                    DevicesJson = JsonSerializer.Serialize(fixtureLaunchSpec.Devices.Select(d => new { HostPath = d.HostPath, ContainerPath = d.ContainerPath })),
                    VolumesJson = JsonSerializer.Serialize(fixtureLaunchSpec.Volumes.Select(v => new { Host = v.Host, Container = v.Container, ReadOnly = v.ReadOnly })),
                });
            }

            foreach (var fixtureModel in fixtureSource.Models)
            {
                db.Models.Add(new Model
                {
                    SourceId = source.Id,
                    PhysicalServerId = physicalServer?.Id,
                    Name = fixtureModel.Name,
                    DisplayName = fixtureModel.DisplayName,
                    ModelSource = fixtureModel.ModelSource,
                    ModelPathOrRepoId = fixtureModel.ModelPathOrRepoId,
                    IsActive = fixtureModel.IsActive,
                    OptionsJson = JsonSerializer.Serialize(fixtureModel.OptionsJson),
                });
            }
        }

        await db.SaveChangesAsync();
    }
}
