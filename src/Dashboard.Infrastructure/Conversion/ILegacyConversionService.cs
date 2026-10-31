namespace Dashboard.Infrastructure.Conversion;

/// <summary>Converts the legacy swap-era rows (physical servers, sources, models)
/// into the new schema's tables (P01b). Not wired into the normal migration chain
/// yet -- P16a adds the migration that calls this and drops the legacy tables.
/// Transactional and idempotent: running it twice creates no duplicates.</summary>
public interface ILegacyConversionService
{
    Task<ConversionReport> ConvertAsync(CancellationToken ct = default);
}
