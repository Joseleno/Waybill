using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Npgsql;
using Waybill.EntityFrameworkCore.Dispatching;
using Waybill.Tests.Integration.G2;

namespace Waybill.Tests.Integration.G4;

// The head pointer's safety net (PLAN.md, "Ponteiro de cabeça"). Waybill never leaves a key's next message unmarked, but
// something outside it can: a trigger disabled, a replica session, a restore, a hand-written UPDATE. On every partition
// upkeep the dispatcher walks a slice of the keys of the partitions it holds and marks any first unfinished message left
// unmarked. Each repair is an anomaly: counted (waybill.outbox.heads_repaired) and logged as an error, with the key.
[Collection(PostgresCollection.Name)]
public sealed class G4_Ponteiro_Rede_ReparaCabecaSemMarca(PostgresFixture postgres)
{
    [Fact]
    public async Task G4_Ponteiro_Rede_CabecaSemMarcaPorAcaoExterna_ReparadaContadaELogada()
    {
        var ct = TestContext.Current.CancellationToken;
        var database = await TestDatabase.CreateAsync(postgres);
        await using var dataSource = NpgsqlDataSource.Create(database.ConnectionString);
        var time = new FakeTimeProvider();
        var transport = new FakeTransport();
        var logs = new LogSink();
        using var metrics = new OutboxMetrics(null);
        var dispatcher = OrderingHarness.Create(dataSource, transport, OrderingHarness.Options(database, partitions: 1), time, logs, metrics);
        Assert.Null(await dispatcher.CheckOrderingAsync(atStartup: true, ct));
        await dispatcher.RunOnceAsync(ct); // takes the partition
        await DispatcherHarness.EnqueueAsync(database, 2, key: _ => "K");

        // Outside Waybill: the head published with the promotion off, so sequence 2 is left unmarked.
        await database.ExecuteAsync("""
            ALTER TABLE waybill.outbox DISABLE TRIGGER outbox_head_promote;
            UPDATE waybill.outbox SET status = 'published', published_at = clock_timestamp() WHERE key = 'K' AND sequence = 1;
            ALTER TABLE waybill.outbox ENABLE TRIGGER outbox_head_promote;
            """);
        Assert.Equal(0, (await dispatcher.RunOnceAsync(ct)).Claimed); // stuck: nothing marked

        time.Advance(TimeSpan.FromMinutes(1)); // the next cycle upkeeps, and walks its partitions' keys
        Assert.Equal(1, (await dispatcher.RunOnceAsync(ct)).Claimed);

        Assert.Equal([2L], transport.Received.Select(m => m.Sequence ?? -1));
        Assert.Equal(1, metrics.HeadsRepaired);
        Assert.Contains(logs.Entries, e => e.Level == LogLevel.Error && e.Message.Contains("key K") && e.Message.Contains("sequence 2"));
    }

    // A slice at a time, from where the last one stopped, back to the first key after the last; only the instance's own
    // partitions are repaired. The empty key is a key like any other.
    [Fact]
    public async Task G4_Ponteiro_Rede_FatiasComCursor_PercorremTodasAsChavesDasParticoesDaInstancia()
    {
        var ct = TestContext.Current.CancellationToken;
        var database = await TestDatabase.CreateAsync(postgres);
        // Five keys, each with its head unmarked; key_hash % 2 puts "", "b" and "d" in partition 0, "a" and "c" in 1.
        await database.ExecuteAsync("""
            INSERT INTO waybill.outbox (id, type, key, key_hash, sequence, payload, content_type, is_head)
            SELECT gen_random_uuid(), 'billing.invoice-paid.v1', k, h, 1, '\x7b7d', 'application/json', false
            FROM (VALUES ('', 0), ('a', 1), ('b', 2), ('c', 3), ('d', 4)) AS v(k, h)
            """);
        await using var dataSource = NpgsqlDataSource.Create(database.ConnectionString);
        var store = new OutboxStore(dataSource);

        var slices = new List<(string? Next, string[] Repaired)>();
        string? after = null;
        do
        {
            var (next, repaired) = await store.RepairHeadsAsync(after, 2, 2, [0], ct);
            slices.Add((next, [.. repaired.Select(r => r.Key)]));
            after = next;
        }
        while (after is not null);

        Assert.Equal(["a", "c", null], slices.Select(s => s.Next));
        Assert.Equal(["", "b", "d"], slices.SelectMany(s => s.Repaired).Order());
        Assert.Equal(2, await database.ScalarAsync("SELECT count(*) FROM waybill.outbox WHERE NOT is_head"));
    }
}
