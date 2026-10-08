using System.Diagnostics;
using Npgsql;
using Waybill.EntityFrameworkCore.Dispatching;
using Waybill.Tests.Integration.G2;

namespace Waybill.Tests.Integration.G4;

// The ordered claim against a large backlog (ADR 0008), measured for OPERATIONS.md: 500 thousand pending rows over 10
// thousand keys, plus a hot key of 50 thousand ahead of them in id order; the ordered claim, and the claim without
// ordering for comparison. Measured on Oct 8, 2026: 3.8 ms against 297 ms, the hot key's queue read and passed over on
// every claim, as with a stopped key (G4_ChaveBloqueadaComCemMilAFrente). Scheduled job only.
[Collection(PostgresCollection.Name)]
public sealed class G4_Backlog_ClaimOrdenadoMedido(PostgresFixture postgres)
{
    private const int Spread = 500_000;
    private const int Hot = 50_000;
    private const int Samples = 10;

    [Fact]
    [Trait("Category", "Long")]
    public async Task G4_Backlog_QuinhentasMilPendentes_LatenciaDoClaimMedida()
    {
        var ct = TestContext.Current.CancellationToken;
        var database = await TestDatabase.CreateAsync(postgres);
        // Numbered as the trigger would have numbered them, in one statement: key k-n gets 1..50 in id order.
        await database.ExecuteAsync($"""
            INSERT INTO waybill.outbox (id, type, key, key_hash, sequence, payload, content_type)
            SELECT ('00000000-0000-7000-8000-' || lpad(to_hex(g), 12, '0'))::uuid, 'billing.invoice-paid.v1',
                   CASE WHEN g <= {Hot} THEN 'hot' ELSE 'k-' || (g % 10000) END,
                   CASE WHEN g <= {Hot} THEN 7 ELSE g % 10000 END,
                   CASE WHEN g <= {Hot} THEN g ELSE (g - {Hot} + 9999) / 10000 END,
                   '\x7b7d', 'application/json'
            FROM generate_series(1, {Spread + Hot}) g;
            INSERT INTO waybill.outbox_keys SELECT key, max(sequence) FROM waybill.outbox GROUP BY key;
            """);
        await database.ExecuteAsync("VACUUM ANALYZE waybill.outbox");
        await using var dataSource = NpgsqlDataSource.Create(database.ConnectionString);
        var store = new OutboxStore(dataSource);
        var unorderedMs = await MeasureAsync(() => store.ClaimAsync("unordered", 100, TimeSpan.FromSeconds(30), ct), store, "unordered");

        var all = OrderingHarness.Create(dataSource, new FakeTransport(), OrderingHarness.Options(database, partitions: 16));
        Assert.Null(await all.CheckOrderingAsync(atStartup: true, ct));
        await all.RunOnceAsync(ct); // takes all 16 partitions (and publishes a first batch)
        var orderedMs = await MeasureAsync(() => store.ClaimAsync(all.Owner, 100, TimeSpan.FromSeconds(30), ct, partitions: 16), store, all.Owner);

        var line = $"backlog {Spread + Hot} (10000 keys + a hot key of {Hot}): claim p50 unordered {unorderedMs:F1} ms, ordered {orderedMs:F1} ms";
        TestContext.Current.TestOutputHelper?.WriteLine(line);
        TestContext.Current.SendDiagnosticMessage(line);
        Assert.Equal(16, all.HeldPartitions.Count);
    }

    private static async Task<double> MeasureAsync(Func<Task<List<ClaimedMessage>>> claim, OutboxStore store, string owner)
    {
        var latencies = new List<double>(Samples);
        for (var i = 0; i < Samples; i++)
        {
            var watch = Stopwatch.StartNew();
            var claimed = await claim();
            latencies.Add(watch.Elapsed.TotalMilliseconds);
            Assert.NotEmpty(claimed);
            await store.ReleaseOwnedAsync(owner, TestContext.Current.CancellationToken);
        }
        latencies.Sort();
        return latencies[Samples / 2];
    }
}
