using Npgsql;
using Waybill.Tests.Integration.G2;
using Waybill.Tests.Integration.G4;

namespace Waybill.Tests.Integration.Convivencia;

// OPERATIONS.md, "Upgrading and rolling back": to go back to the 0.1 binaries, turn ordering off and keep the 0.2 schema.
// The 0.1 dispatcher then drains what 0.2 left behind.
[Collection(PostgresCollection.Name)]
public sealed class Convivencia_Rollback(PostgresFixture postgres)
{
    [Fact]
    public async Task Convivencia_Rollback_01DrenaBacklogNumerado()
    {
        var ct = TestContext.Current.CancellationToken;
        var database = await TestDatabase.CreateAsync(postgres);
        await using var dataSource = NpgsqlDataSource.Create(database.ConnectionString);
        Assert.Null(await OrderingHarness.Create(dataSource, new FakeTransport(), OrderingHarness.Options(database, partitions: 4))
            .CheckOrderingAsync(atStartup: true, ct));

        // Three keys, five messages each, in one transaction: each key's first is its head, the other four wait unmarked.
        var ids = await DispatcherHarness.EnqueueAsync(database, 15, key: n => $"order-{n % 3}");
        Assert.Equal(12, await database.ScalarAsync("SELECT count(*) FROM waybill.outbox WHERE sequence IS NOT NULL AND is_head IS NOT TRUE"));

        await database.ExecuteAsync(OperationsDoc.Sql("ordering-reset")); // every dispatcher stopped, as the SQL requires

        await using var legacy = await LegacyHost.StartAsync("dispatch", database.ConnectionString);
        await legacy.WaitUntilAsync(h => h.Lines("PUBLISHED ").Count >= 15, TimeSpan.FromSeconds(60), "15 messages published");
        await legacy.StopAsync();

        Assert.Equal(ids.Order(), legacy.Lines("PUBLISHED ").Select(Guid.Parse).Order());
        Assert.Equal(15, await database.CountAsync("published"));
    }

    // A message waiting after a basic.return (ADR 0006) is a 0.2 notion: the 0.1 claim does not read next_attempt_at, so
    // after a rollback the wait is gone and the message goes out on the next cycle, as in 0.1. Nothing is lost.
    [Fact]
    public async Task Convivencia_Rollback_LinhaEmEsperaDeReturn_01PublicaNoCicloSeguinte()
    {
        var ct = TestContext.Current.CancellationToken;
        var database = await TestDatabase.CreateAsync(postgres);
        var ids = await DispatcherHarness.EnqueueAsync(database, 1);
        await database.ExecuteAsync("UPDATE waybill.outbox SET next_attempt_at = clock_timestamp() + interval '1 hour'");

        // Control: 0.2 leaves it waiting.
        await using (var dataSource = NpgsqlDataSource.Create(database.ConnectionString))
            Assert.Equal(0, (await DispatcherHarness.Create(dataSource, new FakeTransport(), DispatcherHarness.Options(database)).RunOnceAsync(ct)).Claimed);

        await using var legacy = await LegacyHost.StartAsync("dispatch", database.ConnectionString);
        Assert.Equal(ids[0], Guid.Parse(await legacy.WaitForAsync("PUBLISHED ", TimeSpan.FromSeconds(30))));
        await legacy.StopAsync();
        Assert.Equal(1, await database.CountAsync("published"));
    }
}
