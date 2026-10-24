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

    // A limit, not a promise: a 0.1 dispatcher knows nothing of key heads, and leaves no trace 0.2 could check, so with
    // ordering on it publishes a key's next message while the head is stopped in the DLQ. OPERATIONS.md says to turn
    // ordering on only once no 0.1 dispatcher is left.
    [Fact]
    public async Task Convivencia_Dispatcher01ComOrdenacaoLigada_PublicaForaDeOrdem()
    {
        var ct = TestContext.Current.CancellationToken;
        var database = await TestDatabase.CreateAsync(postgres);
        await using var dataSource = NpgsqlDataSource.Create(database.ConnectionString);
        var dispatcher = OrderingHarness.Create(dataSource, new FakeTransport(), OrderingHarness.Options(database, partitions: 4));
        Assert.Null(await dispatcher.CheckOrderingAsync(atStartup: true, ct));
        var ids = await DispatcherHarness.EnqueueAsync(database, 2, key: _ => "order-9"); // sequences 1 and 2
        await database.ExecuteAsync($"UPDATE waybill.outbox SET status = 'dlq', dlq_reason = 'test' WHERE id = '{ids[0]}'");

        // Control: the 0.2 dispatcher holds the key back behind its head.
        Assert.Equal(0, (await dispatcher.RunOnceAsync(ct)).Claimed);
        Assert.Equal(4, dispatcher.HeldPartitions.Count);
        await dispatcher.ReleaseOwnedAsync(ct);

        await using var legacy = await LegacyHost.StartAsync("dispatch", database.ConnectionString);
        Assert.Equal(ids[1], Guid.Parse(await legacy.WaitForAsync("PUBLISHED ", TimeSpan.FromSeconds(30))));
        await legacy.StopAsync();

        Assert.Equal(1, await database.ScalarAsync($"SELECT count(*) FROM waybill.outbox WHERE id = '{ids[0]}' AND status = 'dlq'"));
        Assert.Equal(1, await database.ScalarAsync($"SELECT count(*) FROM waybill.outbox WHERE id = '{ids[1]}' AND status = 'published'"));
    }
}
