using Npgsql;
using Waybill.EntityFrameworkCore.Dispatching;
using Waybill.Tests.Integration.G2;

namespace Waybill.Tests.Integration.G4;

// M ≥ 2 and a change of owner (PLAN.md 8c-2): the old owner claimed a series (head and successors) and paused past its
// lease; the partition's new owner takes the head alone (a batch of one, or M = 1). The zombie wakes, its fence on a
// successor still valid, and marks it published. Every message still reaches the broker first in sequence order, and
// the key keeps moving: its least unfinished message is marked (ADR 0008).
[Collection(PostgresCollection.Name)]
public sealed class G4_TrocaDeDono_ZumbiComFenceNaSucessora(PostgresFixture postgres)
{
    [Fact]
    public async Task G4_TrocaDeDono_ZumbiComFenceNaSucessora_OrdemEChaveAndando()
    {
        var ct = TestContext.Current.CancellationToken;
        var database = await TestDatabase.CreateAsync(postgres);
        await using var dataSource = NpgsqlDataSource.Create(database.ConnectionString);
        var store = new OutboxStore(dataSource);
        var old = OrderingHarness.Create(dataSource, new FakeTransport(), OrderingHarness.Options(database, partitions: 1, maxPerKey: 3));
        Assert.Null(await old.CheckOrderingAsync(atStartup: true, ct));
        await old.RunOnceAsync(ct); // holds the only partition
        await DispatcherHarness.EnqueueAsync(database, 4, key: _ => "K");
        var series = await store.ClaimAsync(old.Owner, 10, TimeSpan.FromSeconds(30), ct, partitions: 1, maxPerKey: 3);
        Assert.Equal([1L, 2L, 3L], series.Select(c => c.Sequence!.Value));
        var delivered = new List<long> { 1, 2 }; // the zombie's rounds 1 and 2 reached the broker, then it paused
        await database.ExecuteAsync("""
            UPDATE waybill.outbox SET lease_until = clock_timestamp() - interval '1 second' WHERE status = 'claimed';
            UPDATE waybill.outbox_partitions SET lease_until = clock_timestamp() - interval '1 second';
            """);

        var transport = new FakeTransport();
        var successor = OrderingHarness.Create(dataSource, transport, OrderingHarness.Options(database, partitions: 1, maxPerKey: 1));
        Assert.Null(await successor.CheckOrderingAsync(atStartup: true, ct));
        await successor.RunOnceAsync(ct); // takes the partition and the head, whose lease ran out
        delivered.AddRange(transport.Received.Select(m => m.Sequence!.Value));

        // The zombie wakes and records its rounds: the head was claimed again by the successor (fenced off), the
        // successor's row is still its own.
        var finish = await store.FinishAsync(old.Owner, [(series[0], PublishResult.Confirmed), (series[1], PublishResult.Confirmed)],
            ReturnPolicy.From(new WaybillDispatcherOptions()), ct);
        Assert.Equal(1, finish.Fenced);

        var before = transport.Received.Count;
        for (var i = 0; i < 3; i++)
            await successor.RunOnceAsync(ct);
        delivered.AddRange(transport.Received.Skip(before).Select(m => m.Sequence!.Value));

        Assert.Equal([1L, 2L, 3L, 4L], delivered.Distinct());
        Assert.Equal(4, await database.CountAsync("published"));
    }
}
