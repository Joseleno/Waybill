using Npgsql;
using NpgsqlTypes;

namespace Waybill.EntityFrameworkCore.Dispatching;

/// <summary>P and the partition lease, as every dispatcher that orders by key must share them.</summary>
internal readonly record struct OrderingSettings(int Partitions, TimeSpan PartitionLease);

/// <summary>
/// The SQL behind the partition lease (ADR 0007): the global settings, each dispatcher's heartbeat and its partitions.
/// Every timestamp comes from the database clock.
/// </summary>
internal sealed class PartitionStore(NpgsqlDataSource dataSource)
{
    // The first dispatcher creates the settings row and the P partitions in one statement; the others read what it
    // wrote. A data-modifying CTE is invisible to the rest of its statement, hence the UNION with the existing row.
    private const string EnsureSettingsSql = """
        WITH created AS (
            INSERT INTO waybill.settings (id, partitions, partition_lease) VALUES (1, $1, $2)
            ON CONFLICT (id) DO NOTHING
            RETURNING partitions, partition_lease),
        partitions AS (
            INSERT INTO waybill.outbox_partitions (partition, epoch)
            SELECT g, 0 FROM created, generate_series(0, created.partitions - 1) AS g)
        SELECT partitions, partition_lease FROM created
        UNION ALL
        SELECT partitions, partition_lease FROM waybill.settings WHERE id = 1 AND NOT EXISTS (SELECT 1 FROM created)
        """;

    private const string ReadSettingsSql = "SELECT partitions, partition_lease FROM waybill.settings WHERE id = 1";

    private const string HeartbeatSql = """
        INSERT INTO waybill.outbox_instances (owner, heartbeat_at) VALUES ($1, clock_timestamp())
        ON CONFLICT (owner) DO UPDATE SET heartbeat_at = excluded.heartbeat_at
        """;

    // A dead instance stops counting after one partition lease; its row goes long after, so a slow one is not
    // collected while it may still renew.
    private const string CollectSql = "DELETE FROM waybill.outbox_instances WHERE heartbeat_at < clock_timestamp() - $1 * 10";

    // Only partitions still held: one whose lease ran out is lost, even if nobody took it yet, and is acquired again
    // with a new epoch.
    private const string RenewSql = """
        UPDATE waybill.outbox_partitions SET lease_until = clock_timestamp() + $2
        WHERE owner = $1 AND lease_until > clock_timestamp()
        RETURNING partition, epoch
        """;

    private const string LiveSql = "SELECT count(*)::int FROM waybill.outbox_instances WHERE heartbeat_at > clock_timestamp() - $1";

    // Free or expired partitions, as many as the fair share still allows. As in the row claim (ADR 0001), the condition
    // sits with the FOR UPDATE and is repeated in the outer UPDATE: two instances never acquire the same partition.
    private const string AcquireSql = """
        WITH free AS MATERIALIZED (
            SELECT partition FROM waybill.outbox_partitions
            WHERE owner IS NULL OR lease_until < clock_timestamp()
            ORDER BY partition
            LIMIT $3
            FOR UPDATE SKIP LOCKED)
        UPDATE waybill.outbox_partitions p
        SET owner = $1, epoch = p.epoch + 1, lease_until = clock_timestamp() + $2
        FROM free
        WHERE p.partition = free.partition AND (p.owner IS NULL OR p.lease_until < clock_timestamp())
        RETURNING p.partition, p.epoch
        """;

    private const string ReleaseSql = """
        UPDATE waybill.outbox_partitions SET owner = NULL, lease_until = NULL
        WHERE owner = $1 AND partition = ANY($2)
        """;

    private const string LeavePartitionsSql = "UPDATE waybill.outbox_partitions SET owner = NULL, lease_until = NULL WHERE owner = $1";

    private const string LeaveInstancesSql = "DELETE FROM waybill.outbox_instances WHERE owner = $1";

    /// <summary>Creates the settings and the partitions if this is the first dispatcher to order; returns what is stored.</summary>
    public async Task<OrderingSettings> EnsureSettingsAsync(OrderingSettings wanted, CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand(EnsureSettingsSql);
        command.Parameters.Add(new NpgsqlParameter { Value = wanted.Partitions });
        command.Parameters.Add(new NpgsqlParameter { Value = wanted.PartitionLease, NpgsqlDbType = NpgsqlDbType.Interval });
        // Started together with another instance: its INSERT won, ours waited for its commit and did nothing, but our
        // statement's snapshot predates that commit. A new statement takes a new snapshot and sees the row.
        return await ReadSettingsAsync(command, cancellationToken).ConfigureAwait(false)
            ?? await ReadSettingsAsync(cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("waybill.settings was neither created nor found.");
    }

    /// <summary>The stored settings, or null when no dispatcher orders by key.</summary>
    public async Task<OrderingSettings?> ReadSettingsAsync(CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand(ReadSettingsSql);
        return await ReadSettingsAsync(command, cancellationToken).ConfigureAwait(false);
    }

    // The trigger that numbers keyed rows while ordering is on (ADR 0008). 'O' fires in ordinary sessions, 'A' always;
    // 'D' (ALTER TABLE … DISABLE TRIGGER) and 'R' (replica only) leave keyed rows unnumbered, so they go out unordered.
    private const string NumberingTriggerSql =
        "SELECT tgenabled FROM pg_trigger WHERE tgrelid = 'waybill.outbox'::regclass AND tgname = 'outbox_sequence'";

    /// <summary>Whether the numbering trigger exists and fires for the applications' sessions.</summary>
    public async Task<bool> NumberingTriggerEnabledAsync(CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand(NumberingTriggerSql);
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is char state && state is 'O' or 'A';
    }

    /// <summary>
    /// One round of partition upkeep, before a claim and with nothing in flight: heartbeat, collection of long-dead
    /// instances, renewal, then acquisition up to the fair share <c>ceil(P / live)</c> or release of what exceeds it.
    /// </summary>
    /// <returns>The partitions this instance holds afterwards, with their epochs, by partition.</returns>
    public async Task<SortedDictionary<int, long>> MaintainAsync(string owner, OrderingSettings settings, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var lease = new NpgsqlParameter { Value = settings.PartitionLease, NpgsqlDbType = NpgsqlDbType.Interval };

        var held = new SortedDictionary<int, long>();
        int live;
        await using (var upkeep = connection.CreateBatch())
        {
            upkeep.BatchCommands.Add(Command(HeartbeatSql, owner));
            upkeep.BatchCommands.Add(Command(CollectSql, lease.Clone()));
            await upkeep.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using (var batch = connection.CreateBatch())
        {
            batch.BatchCommands.Add(Command(RenewSql, owner, lease.Clone()));
            batch.BatchCommands.Add(Command(LiveSql, lease.Clone()));
            await using var reader = await batch.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                held[reader.GetInt32(0)] = reader.GetInt64(1);
            await reader.NextResultAsync(cancellationToken).ConfigureAwait(false);
            await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            live = Math.Max(reader.GetInt32(0), 1);
        }

        var share = (settings.Partitions + live - 1) / live;
        if (held.Count < share)
        {
            await using var acquire = new NpgsqlCommand(AcquireSql, connection);
            acquire.Parameters.Add(new NpgsqlParameter { Value = owner });
            acquire.Parameters.Add(lease.Clone());
            acquire.Parameters.Add(new NpgsqlParameter { Value = share - held.Count });
            await using var reader = await acquire.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                held[reader.GetInt32(0)] = reader.GetInt64(1);
        }
        else if (held.Count > share)
        {
            var extra = held.Keys.Skip(share).ToArray();
            await using var release = new NpgsqlCommand(ReleaseSql, connection);
            release.Parameters.Add(new NpgsqlParameter { Value = owner });
            release.Parameters.Add(new NpgsqlParameter { Value = extra });
            await release.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            foreach (var partition in extra)
                held.Remove(partition);
        }

        return held;
    }

    /// <summary>Hands back every partition of <paramref name="owner"/> and removes it from the live instances (shutdown).</summary>
    public async Task LeaveAsync(string owner, CancellationToken cancellationToken)
    {
        await using var batch = dataSource.CreateBatch();
        batch.BatchCommands.Add(Command(LeavePartitionsSql, owner));
        batch.BatchCommands.Add(Command(LeaveInstancesSql, owner));
        await batch.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<OrderingSettings?> ReadSettingsAsync(NpgsqlCommand command, CancellationToken cancellationToken)
    {
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? new OrderingSettings(reader.GetInt32(0), reader.GetFieldValue<TimeSpan>(1))
            : null;
    }

    private static NpgsqlBatchCommand Command(string sql, params object[] parameters)
    {
        var command = new NpgsqlBatchCommand(sql);
        foreach (var parameter in parameters)
            command.Parameters.Add(parameter as NpgsqlParameter ?? new NpgsqlParameter { Value = parameter });
        return command;
    }
}
