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
}
