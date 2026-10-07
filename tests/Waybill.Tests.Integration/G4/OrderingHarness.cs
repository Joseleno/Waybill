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
        NpgsqlDataSource dataSource, ITransport transport, WaybillDispatcherOptions options, TimeProvider? time = null) =>
        DispatcherHarness.Create(dataSource, transport, options, time: time, orderByKey: true);

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
