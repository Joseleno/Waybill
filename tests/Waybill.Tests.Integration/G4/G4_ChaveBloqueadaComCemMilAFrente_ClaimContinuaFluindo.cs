using System.Diagnostics;
using System.Text.Json;
using Npgsql;
using Waybill.EntityFrameworkCore.Dispatching;
using Waybill.Tests.Integration.G2;

namespace Waybill.Tests.Integration.G4;

// A key stopped by the DLQ with a long queue behind it, all of it older than everything else: the claim walks
// ix_outbox_claimable in id order and reads, then passes over, every row of that queue, on every cycle, until the key is
// released (ADR 0008). The other keys must keep flowing; the price of reading past the queue is measured and bounded here.
[Collection(PostgresCollection.Name)]
public sealed class G4_ChaveBloqueadaComCemMilAFrente_ClaimContinuaFluindo(PostgresFixture postgres)
{
    private const int Queue = 100_000;
    private const int Samples = 5;

    [Fact]
    public async Task G4_ChaveBloqueadaComCemMilAFrente_OutrasChavesFluem_CustoMedido()
    {
        var ct = TestContext.Current.CancellationToken;
        var database = await TestDatabase.CreateAsync(postgres);
        // Written before ordering is on, with their sequences given: ids smaller than any the client makes, so the whole
        // queue sits ahead in claim order; its head is in the DLQ.
        await database.ExecuteAsync($"""
            INSERT INTO waybill.outbox (id, type, key, key_hash, sequence, payload, content_type, status, attempts, dlq_reason)
            SELECT ('00000000-0000-7000-8000-' || lpad(to_hex(g), 12, '0'))::uuid, 'billing.invoice-paid.v1', 'blocked', 0, g,
                   '\x7b7d', 'application/json', CASE WHEN g = 1 THEN 'dlq' ELSE 'pending' END,
                   CASE WHEN g = 1 THEN 1 ELSE 0 END, CASE WHEN g = 1 THEN 'defect' END
            FROM generate_series(1, {Queue + 1}) g;
            INSERT INTO waybill.outbox_keys VALUES ('blocked', {Queue + 1});
            """);
        await using var dataSource = NpgsqlDataSource.Create(database.ConnectionString);
        var transport = new FakeTransport();
        var dispatcher = OrderingHarness.Create(dataSource, transport, OrderingHarness.Options(database, partitions: 4));
        Assert.Null(await dispatcher.CheckOrderingAsync(atStartup: true, ct));
        Assert.Equal(0, (await dispatcher.RunOnceAsync(ct)).Claimed); // takes the partitions; nothing of the stopped key
        await DispatcherHarness.EnqueueAsync(database, 100, key: i => $"order-{i}");
        await database.ExecuteAsync("VACUUM ANALYZE waybill.outbox");
        var store = new OutboxStore(dataSource);

        var latencies = new List<double>(Samples);
        for (var i = 0; i < Samples; i++)
        {
            var watch = Stopwatch.StartNew();
            var claimed = await store.ClaimAsync(dispatcher.Owner, 100, TimeSpan.FromSeconds(30), ct, partitions: 4);
            latencies.Add(watch.Elapsed.TotalMilliseconds);
            Assert.Equal(100, claimed.Count);
            Assert.DoesNotContain(claimed, c => c.Message.Key == "blocked");
            await store.ReleaseOwnedAsync(dispatcher.Owner, ct);
        }
        var buffers = await ClaimBuffersAsync(dataSource, dispatcher.Owner);

        latencies.Sort();
        TestContext.Current.TestOutputHelper?.WriteLine(
            $"ordered claim with a stopped key of {Queue} rows ahead: p50 {latencies[Samples / 2]:F1} ms, max {latencies[^1]:F1} ms, {buffers} buffers");
        // Measured on Oct 8, 2026: about 4 buffers and 12 µs per row of the stopped queue (10 thousand: p50 120 ms; 100
        // thousand: p50 1.19 s), linear and above the 50 ms the stage set, so a head pointer comes in its own PR before
        // 8c-2 (PLAN.md). Until then this bounds the cost from regressing.
        Assert.True(buffers < 5L * Queue, $"{buffers} buffers for a stopped queue of {Queue} rows");
    }

    // Shared buffers the claim touches, from EXPLAIN (ANALYZE, BUFFERS), in a transaction rolled back so nothing stays claimed.
    private static async Task<long> ClaimBuffersAsync(NpgsqlDataSource dataSource, string owner)
    {
        await using var connection = await dataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using var command = new NpgsqlCommand("EXPLAIN (ANALYZE, BUFFERS, FORMAT JSON) " + OutboxStore.ClaimOrderedSql, connection, transaction);
        command.Parameters.Add(new NpgsqlParameter { Value = owner });
        command.Parameters.Add(new NpgsqlParameter { Value = TimeSpan.FromSeconds(30), NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Interval });
        command.Parameters.Add(new NpgsqlParameter { Value = 100 });
        command.Parameters.Add(new NpgsqlParameter { Value = 4 });
        var plan = JsonDocument.Parse((string)(await command.ExecuteScalarAsync())!).RootElement[0].GetProperty("Plan");
        await transaction.RollbackAsync();
        return plan.GetProperty("Shared Hit Blocks").GetInt64() + plan.GetProperty("Shared Read Blocks").GetInt64();
    }
}
