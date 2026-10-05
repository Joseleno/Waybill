using Npgsql;

namespace Waybill.EntityFrameworkCore;

/// <summary>
/// The claim and the retention are single autocommit statements that rely on READ COMMITTED (ADR 0001): under
/// REPEATABLE READ or SERIALIZABLE, a row changed by a concurrent transaction fails with 40001 instead of being
/// re-checked. Their level is the session default, so a database or role configured otherwise is caught before the
/// first cycle instead of failing every cycle after it.
/// </summary>
internal static class IsolationLevelCheck
{
    public const string Required = "read committed";

    public static async Task<string> DefaultAsync(NpgsqlDataSource dataSource, CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand("SHOW default_transaction_isolation");
        return (string)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
    }
}
