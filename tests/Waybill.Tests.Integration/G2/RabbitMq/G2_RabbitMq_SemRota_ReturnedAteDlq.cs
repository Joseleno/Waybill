using Npgsql;

namespace Waybill.Tests.Integration.G2.RabbitMq;

// mandatory + no matching binding: the broker returns the message (basic.return). Each return spends one of
// MaxReturns; the last one sends it to the outbox DLQ with the broker's reply text.
[Collection(BrokerCollection.Name)]
public sealed class G2_RabbitMq_SemRota_ReturnedAteDlq(PostgresFixture postgres, RabbitMqFixture rabbit)
{
    [Fact]
    public async Task G2_RabbitMq_SemRota_ReturnedAteDlq_ComNoRoute()
    {
        var ct = TestContext.Current.CancellationToken;
        var database = await TestDatabase.CreateAsync(postgres);
        var (exchange, queue) = await rabbit.DeclareTopologyAsync("audit.#"); // nothing binds billing.*
        await DispatcherHarness.EnqueueAsync(database, 1);
        await using var dataSource = NpgsqlDataSource.Create(database.ConnectionString);
        await using var transport = G2_RabbitMq_PublicaComConfirmacaoEPropriedades.Transport(rabbit, exchange);
        var dispatcher = DispatcherHarness.Create(dataSource, transport, DispatcherHarness.Options(database, o => o.MaxReturns = 2));

        await dispatcher.RunOnceAsync(ct);
        Assert.Equal(1, await database.ScalarAsync("SELECT count(*) FROM waybill.outbox WHERE status = 'pending' AND attempts = 1"));

        await dispatcher.RunOnceAsync(ct);
        Assert.Equal(1, await database.ScalarAsync(
            "SELECT count(*) FROM waybill.outbox WHERE status = 'dlq' AND attempts = 2 AND dlq_reason LIKE '312 NO_ROUTE%'"));
        Assert.Empty(await rabbit.DrainAsync(queue));
    }
}
