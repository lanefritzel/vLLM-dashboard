using System.Text.Json.Serialization;

namespace Dashboard.Core.Supervisor;

/// <summary>The shape of a single push the supervisor POSTs to the dashboard's
/// receiver (P10b, doc 5): <c>{deployment_id, seq, ts, kind, ...}</c> where
/// the trailing fields vary by <see cref="Kind"/> -- "state" carries
/// <see cref="State"/>/<see cref="Detail"/>, "report" carries
/// <see cref="Report"/>, "log" carries <see cref="Lines"/>.
///
/// Bound from the raw POST body by ASP.NET's minimal-API JSON binding
/// (Program.cs's <c>MapPost("/internal/supervisor-events", ...)</c>), which
/// uses the app's default (camelCase-ish) options -- NOT
/// <see cref="Dashboard.Infrastructure.Supervisor.SupervisorClient"/>'s own
/// explicit <c>JsonNamingPolicy.SnakeCaseLower</c>, which only applies to
/// that class's own outbound HTTP client calls. A since-corrected claim that
/// this model was "deserialized with SnakeCaseLower" was never actually true
/// for this one (P16a finding): with no explicit naming policy configured
/// for this endpoint, `deployment_id` never matched `DeploymentId` under the
/// default camelCase convention, so the one `required` property with an
/// underscore in its wire name failed to bind on every single push -- a 400
/// returned by the framework itself, before this record's own consumer
/// (<see cref="Dashboard.Infrastructure.Persistence.V2.SupervisorEventReceiver"/>)
/// ever ran. Every other field happens to be a single lowercase word, so it
/// coincidentally matched regardless of policy, which is why this went
/// unnoticed -- state events only ever looked like they worked because
/// `DeploymentCatchUpService`'s independent polling backfilled them; log
/// lines have no such fallback and were always silently missing. Explicit
/// <see cref="JsonPropertyNameAttribute"/>s here pin the wire contract
/// directly to this one model, independent of whatever naming policy (or
/// lack of one) the app's JSON options end up using.</summary>
public sealed record SupervisorPushEvent
{
    /// <summary>The dashboard's own deployments.id, as a string (P10a passes
    /// it to the supervisor; the supervisor only ever echoes it back).</summary>
    [JsonPropertyName("deployment_id")]
    public required string DeploymentId { get; init; }

    [JsonPropertyName("seq")]
    public int Seq { get; init; }

    [JsonPropertyName("ts")]
    public double Ts { get; init; }

    /// <summary>"state" | "report" | "log".</summary>
    [JsonPropertyName("kind")]
    public required string Kind { get; init; }

    [JsonPropertyName("state")]
    public string? State { get; init; }

    [JsonPropertyName("detail")]
    public Dictionary<string, object?>? Detail { get; init; }

    [JsonPropertyName("report")]
    public Dictionary<string, object?>? Report { get; init; }

    [JsonPropertyName("lines")]
    public List<string>? Lines { get; init; }
}
