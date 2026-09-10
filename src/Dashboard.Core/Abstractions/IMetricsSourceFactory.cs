using Dashboard.Core.Models;

namespace Dashboard.Core.Abstractions;

/// <summary>Creates the right <see cref="IMetricsSource"/> for a given source config.</summary>
public interface IMetricsSourceFactory
{
    IMetricsSource Create(SourceInfo source);
}
