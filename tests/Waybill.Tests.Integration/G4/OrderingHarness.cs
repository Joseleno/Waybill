using Npgsql;
using Waybill.EntityFrameworkCore.Dispatching;
using Waybill.Tests.Integration.G2;

namespace Waybill.Tests.Integration.G4;

/// <summary>Dispatchers that order by key over the test database, driven cycle by cycle by the test.</summary>
public static class OrderingHarness
{
    public static WaybillDispatcherOptions Options(TestDatabase database, int partitions, TimeSpan? partitionLease = null) =>
        DispatcherHarness.Options(database, o =>
        {
            o.Partitions = partitions;
            o.PartitionLease = partitionLease ?? TimeSpan.FromSeconds(60);
        });

    internal static OutboxDispatcher Create(
        NpgsqlDataSource dataSource, ITransport transport, WaybillDispatcherOptions options, TimeProvider? time = null, LogSink? logs = null, OutboxMetrics? metrics = null) =>
        DispatcherHarness.Create(dataSource, transport, options, time: time, orderByKey: true, logs: logs, metrics: metrics);

    /// <summary>Shared buffers one ordered claim touches, from EXPLAIN (ANALYZE, BUFFERS), rolled back so nothing stays claimed.</summary>
    public static async Task<long> ClaimBuffersAsync(NpgsqlDataSource dataSource, string owner, int partitions, int batchSize = 100)
    {
        await using var connection = await dataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using var command = new NpgsqlCommand("EXPLAIN (ANALYZE, BUFFERS, FORMAT JSON) " + OutboxStore.ClaimOrderedSql, connection, transaction);
        command.Parameters.Add(new NpgsqlParameter { Value = owner });
        command.Parameters.Add(new NpgsqlParameter { Value = TimeSpan.FromSeconds(30), NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Interval });
        command.Parameters.Add(new NpgsqlParameter { Value = batchSize });
        command.Parameters.Add(new NpgsqlParameter { Value = partitions });
        var plan = System.Text.Json.JsonDocument.Parse((string)(await command.ExecuteScalarAsync())!).RootElement[0].GetProperty("Plan");
        await transaction.RollbackAsync();
        return plan.GetProperty("Shared Hit Blocks").GetInt64() + plan.GetProperty("Shared Read Blocks").GetInt64();
    }

    /// <summary>
    /// Marks rows written around the triggers (generate_series seeds with their sequences given) as the triggers would:
    /// of each key, only its least unfinished row. A seed left at the column's default (true) would be a state the
    /// triggers never produce, and the claim would read the whole queue.
    /// </summary>
    public static Task MarkHeadsAsync(TestDatabase database) =>
        database.ExecuteAsync("""
            UPDATE waybill.outbox o SET is_head = (o.sequence = (
                SELECT min(h.sequence) FROM waybill.outbox h
                WHERE h.key = o.key AND h.sequence IS NOT NULL AND h.status IN ('pending', 'claimed', 'dlq')))
            WHERE o.sequence IS NOT NULL AND o.status IN ('pending', 'claimed', 'dlq')
            """);

    /// <summary>The partition of each message, as the claim computes it: key_hash % P.</summary>
    public static async Task<Dictionary<Guid, int>> PartitionOfAsync(TestDatabase database, int partitions)
    {
        await using var connection = new NpgsqlConnection(database.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand($"SELECT id, key_hash % {partitions} FROM waybill.outbox WHERE key IS NOT NULL", connection);
        await using var reader = await command.ExecuteReaderAsync();
        var partitionOf = new Dictionary<Guid, int>();
        while (await reader.ReadAsync())
            partitionOf[reader.GetGuid(0)] = reader.GetInt32(1);
        return partitionOf;
    }
}
