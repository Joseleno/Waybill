using Npgsql;
using Waybill.Tests.Integration.G2;
using Waybill.Tests.Integration.G4;

namespace Waybill.Tests.Integration.Convivencia;

// Ordering by key is set on the dispatchers only (OPERATIONS.md, "Ordering by key"), so it can be turned on while some
// applications still enqueue with 0.1.
[Collection(PostgresCollection.Name)]
public sealed class Convivencia_Ordenacao01(PostgresFixture postgres)
{
    // The database numbers keyed rows, whoever writes them: a 0.1 producer's messages of one key, each in a transaction of
    // its own, get sequences 1 to 10 and go out in that order.
    [Fact]
    public async Task Convivencia_Produtor01ComOrdenacaoLigada_SaiEmOrdem()
    {
        var ct = TestContext.Current.CancellationToken;
        var database = await TestDatabase.CreateAsync(postgres);
        await using var dataSource = NpgsqlDataSource.Create(database.ConnectionString);
        var transport = new FakeTransport();
        var dispatcher = OrderingHarness.Create(dataSource, transport, OrderingHarness.Options(database, partitions: 4));
        Assert.Null(await dispatcher.CheckOrderingAsync(atStartup: true, ct));

        await using (var producer = await LegacyHost.StartAsync("produce", database.ConnectionString, "10", "order-7"))
        {
            await producer.ExitedAsync();
            var enqueued = producer.Lines("ENQUEUED ").Select(Guid.Parse).ToList();

            for (var cycle = 0; cycle < 100 && await database.CountAsync("published") < 10; cycle++)
                await dispatcher.RunOnceAsync(ct);

            Assert.Equal(Enumerable.Range(1, 10).Select(i => (long?)i), transport.Received.Select(m => m.Sequence));
            Assert.Equal(enqueued, transport.Received.Select(m => m.MessageId));
        }
    }
}
