using Npgsql;

namespace Waybill.Tests.Integration.G2.RabbitMq;

// Missing topology is a configuration problem, not a defect of the messages: they wait in the outbox, spending no
// attempt, and flow as soon as the exchange exists.
[Collection(BrokerCollection.Name)]
public sealed class G2_RabbitMq_ExchangeInexistente_RetrySemGastarTentativa(PostgresFixture postgres, RabbitMqFixture rabbit)
{
    [Fact]
    public async Task G2_RabbitMq_ExchangeInexistente_RetrySemGastarTentativa_DepoisPublica()
    {
        var ct = TestContext.Current.CancellationToken;
        var database = await TestDatabase.CreateAsync(postgres);
        var (exchange, queue) = await rabbit.DeclareTopologyAsync(declareExchange: false);
        await DispatcherHarness.EnqueueAsync(database, 2);
        await using var dataSource = NpgsqlDataSource.Create(database.ConnectionString);
        await using var transport = G2_RabbitMq_PublicaComConfirmacaoEPropriedades.Transport(rabbit, exchange);
        var dispatcher = DispatcherHarness.Create(dataSource, transport, DispatcherHarness.Options(database));

        for (var cycle = 0; cycle < 2; cycle++)
        {
            await dispatcher.RunOnceAsync(ct);
            Assert.Equal(2, await database.ScalarAsync("SELECT count(*) FROM waybill.outbox WHERE status = 'pending' AND attempts = 0"));
        }

        await rabbit.DeclareExchangeAsync(exchange, queue);
        await dispatcher.RunOnceAsync(ct);

        Assert.Equal(2, await database.CountAsync("published"));
        Assert.Equal(2, (await rabbit.DrainAsync(queue)).Count);
    }
}
