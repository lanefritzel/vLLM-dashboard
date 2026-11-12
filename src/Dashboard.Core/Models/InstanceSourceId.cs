namespace Dashboard.Core.Models;

/// <summary>The "instance-{id}" id format <c>InstanceMetricsPollingService</c> uses to key
/// a V2 instance's samples in <see cref="Dashboard.Core.Abstractions.IMetricsService"/>/
/// <c>MetricsHub</c>. Lives in Core (not Web, where it originated) so the llama.cpp proxy
/// code in Dashboard.Infrastructure -- which cannot reference Dashboard.Web -- can target
/// the same id without duplicating the format string.</summary>
public static class InstanceSourceId
{
    public static string For(int instanceId) => "instance-" + instanceId;
}
