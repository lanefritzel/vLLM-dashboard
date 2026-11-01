using Dashboard.Core.Abstractions;
using Microsoft.Extensions.Configuration;

namespace Dashboard.Infrastructure.Persistence.V2;

/// <summary>P09b's "for now" implementation: one token for every instance, read
/// from configuration (appsettings/env var "Supervisor:Token") -- not the DB,
/// since per-instance token storage is P15b's own open decision. Swapping this
/// out for a real per-instance lookup later doesn't require touching any
/// caller, only this class (and DI registration).</summary>
public sealed class ConfigSupervisorTokenProvider : ISupervisorTokenProvider
{
    private readonly string? _token;

    public ConfigSupervisorTokenProvider(IConfiguration configuration) =>
        _token = configuration["Supervisor:Token"];

    public string? GetToken(int instanceId) => _token;
}
