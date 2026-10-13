using System.Diagnostics;
using Npgsql;
using Waybill.EntityFrameworkCore.Dispatching;
using Waybill.Tests.Integration.G2;

namespace Waybill.Tests.Integration.G4;

// A key stopped by the DLQ with a long queue behind it, all of it older than everything else (ADR 0008). Without the head
// pointer the claim read and passed over every row of that queue, on every cycle, until the key was released (measured
// on Oct 8, 2026: about 4 buffers and 12 µs per row; 100 thousand, p50 1.19 s). With it, only the head is in
// ix_outbox_claimable: the other keys flow, and the claim reads the same few buffers whatever the size of the queue.
// Releasing the head marks the next row of the key, which the following claim takes.
[Collection(PostgresCollection.Name)]
public sealed class G4_ChaveBloqueadaComCemMilAFrente_ClaimContinuaFluindo(PostgresFixture postgres)
{
    private const int Samples = 5;

    [Theory]
    [InlineData(10_000)]
    [InlineData(100_000)]
    public async Task G4_ChaveBloqueadaComCemMilAFrente_OutrasChavesFluem_CustoMedido(int queue)
    {
        var ct = TestContext.Current.CancellationToken;
        var database = await TestDatabase.CreateAsync(postgres);
        // Written before ordering is on, with their sequences given: ids smaller than any the client makes, so the whole
        // queue sits ahead in claim order; its head is in the DLQ. Marked as the triggers would have.
        await database.ExecuteAsync($"""
            INSERT INTO waybill.outbox (id, type, key, key_hash, sequence, payload, content_type, status, attempts, dlq_reason)
            SELECT ('00000000-0000-7000-8000-' || lpad(to_hex(g), 12, '0'))::uuid, 'billing.invoice-paid.v1', 'blocked', 0, g,
                   '\x7b7d', 'application/json', CASE WHEN g = 1 THEN 'dlq' ELSE 'pending' END,
                   CASE WHEN g = 1 THEN 1 ELSE 0 END, CASE WHEN g = 1 THEN 'defect' END
            FROM generate_series(1, {queue + 1}) g;
            INSERT INTO waybill.outbox_keys VALUES ('blocked', {queue + 1});
            """);
        await OrderingHarness.MarkHeadsAsync(database);
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
        var buffers = await OrderingHarness.ClaimBuffersAsync(dataSource, dispatcher.Owner, partitions: 4);

        latencies.Sort();
        TestContext.Current.TestOutputHelper?.WriteLine(
            $"ordered claim with a stopped key of {queue} rows ahead: p50 {latencies[Samples / 2]:F1} ms, max {latencies[^1]:F1} ms, {buffers} buffers");
        // The latency is only recorded (CI machines vary); the buffers are the oracle. The claim of 100 rows reads some
        // tens of buffers per row it takes (head and hot-key probes, partition, the join on the primary key, with the dead
        // versions left by the samples above) and nothing per row of the stopped queue: about 3.4 thousand with 10
        // thousand stopped, 4.5 thousand with 100 thousand (deeper indexes), against some 400 thousand before the pointer.
        Assert.True(buffers < 10_000, $"{buffers} buffers with a stopped queue of {queue} rows");

        await using (var release = dataSource.CreateCommand(OperationsDoc.Sql("key-release").Replace("order-42", "blocked")))
            await release.ExecuteNonQueryAsync(ct);
        var next = await store.ClaimAsync(dispatcher.Owner, 200, TimeSpan.FromSeconds(30), ct, partitions: 4);
        Assert.Equal([2L], next.Where(c => c.Message.Key == "blocked").Select(c => c.Message.Sequence));
    }
}
