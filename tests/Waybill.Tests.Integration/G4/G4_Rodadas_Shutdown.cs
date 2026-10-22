using Npgsql;
using Waybill.EntityFrameworkCore.Dispatching;
using Waybill.Tests.Integration.G2;

namespace Waybill.Tests.Integration.G4;

// Stopping between rounds (PLAN.md 8c-2): no round starts after the host asked to stop, and what the rounds already
// published is recorded as published. Handed back instead, at shutdown, it would be published again by the next owner:
// up to M copies of a key's messages on every deploy.
[Collection(PostgresCollection.Name)]
public sealed class G4_Rodadas_Shutdown(PostgresFixture postgres)
{
    [Fact]
    public async Task G4_ShutdownEntreRodadas_RodadaUmMarcadaSemRepublicar()
    {
        using var stopping = new CancellationTokenSource();
        var (database, dispatcher, transport) = await DispatcherAsync((call, batch) =>
        {
            stopping.Cancel(); // the host asks to stop while round 1 is out
            return FakeTransport.ConfirmAll(batch);
        });
        await DispatcherHarness.EnqueueAsync(database, 3, key: _ => "K");

        var cycle = await dispatcher.RunOnceAsync(stopping.Token);
        await dispatcher.ReleaseOwnedAsync(CancellationToken.None);

        Assert.Equal(1, transport.Calls);
        Assert.Equal(1, cycle.Claimed > 0 ? await database.CountAsync("published") : -1);
        Assert.Equal(0, await database.ScalarAsync("SELECT count(*) FROM waybill.outbox WHERE sequence > 1 AND (status <> 'pending' OR attempts > 0)"));
    }

    // Anything that throws in the dispatcher after a round was confirmed (here, its clock) still lets the marking record
    // that round: the exception surfaces, and the cycle is retried, but nothing confirmed is published again.
    [Fact]
    public async Task G4_ExcecaoEntreRodadas_RodadaUmMarcada()
    {
        var ct = TestContext.Current.CancellationToken;
        var clock = new ThrowingClock();
        var (database, dispatcher, transport) = await DispatcherAsync((call, batch) =>
        {
            clock.Armed = true; // the next reading of the clock, right after round 1, throws
            return FakeTransport.ConfirmAll(batch);
        }, clock);
        await DispatcherHarness.EnqueueAsync(database, 3, key: _ => "K");

        await Assert.ThrowsAsync<InvalidOperationException>(() => dispatcher.RunOnceAsync(ct));

        Assert.Equal(1, transport.Calls);
        Assert.Equal(1, await database.CountAsync("published"));
        Assert.Equal(0, await database.ScalarAsync("SELECT count(*) FROM waybill.outbox WHERE sequence > 1 AND (status <> 'pending' OR attempts > 0)"));
    }

    private sealed class ThrowingClock : TimeProvider
    {
        public bool Armed { get; set; }

        public override long GetTimestamp()
        {
            if (Armed)
            {
                Armed = false;
                throw new InvalidOperationException("the clock failed");
            }
            return base.GetTimestamp();
        }
    }

    private async Task<(TestDatabase, OutboxDispatcher, FakeTransport)> DispatcherAsync(
        Func<int, IReadOnlyList<OutgoingMessage>, Task<IReadOnlyList<PublishResult>>> behavior, TimeProvider? time = null)
    {
        var database = await TestDatabase.CreateAsync(postgres);
        var dataSource = NpgsqlDataSource.Create(database.ConnectionString);
        var transport = new FakeTransport((call, batch, _) => behavior(call, batch));
        var dispatcher = OrderingHarness.Create(dataSource, transport, OrderingHarness.Options(database, partitions: 4, maxPerKey: 3), time);
        Assert.Null(await dispatcher.CheckOrderingAsync(atStartup: true, TestContext.Current.CancellationToken));
        Assert.Equal(0, (await dispatcher.RunOnceAsync(TestContext.Current.CancellationToken)).Claimed); // takes the partitions
        return (database, dispatcher, transport);
    }
}
