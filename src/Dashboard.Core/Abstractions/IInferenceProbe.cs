using Dashboard.Core.Models;

namespace Dashboard.Core.Abstractions;

/// <summary>Runs a quick inference probe against a source's inference endpoint.</summary>
public interface IInferenceProbe
{
    Task<InferenceResult> ProbeAsync(SourceInfo source, InferenceRequest request, CancellationToken ct = default);
}
