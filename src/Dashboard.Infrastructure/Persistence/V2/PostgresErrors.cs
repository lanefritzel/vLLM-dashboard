using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Dashboard.Infrastructure.Persistence.V2;

/// <summary>Shared helper (P09b) so every V2 service turns a raw Postgres unique
/// violation into the same kind of friendly message, instead of a 23505 error
/// code and a constraint name leaking up to a caller.</summary>
internal static class PostgresErrors
{
    private const string UniqueViolation = "23505";

    public static bool IsUniqueViolation(DbUpdateException ex) =>
        ex.InnerException is PostgresException { SqlState: UniqueViolation };
}
