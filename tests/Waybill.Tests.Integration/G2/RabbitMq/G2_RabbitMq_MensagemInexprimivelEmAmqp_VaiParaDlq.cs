using Npgsql;

namespace Waybill.Tests.Integration.G2.RabbitMq;

// A row the broker can never carry (here a correlation id longer than an AMQP short string, written around the
// enqueue checks) is a defect of the message: it goes to the DLQ with the reason, instead of an endless retry that
// would spend no attempt and never surface. The rest of the batch is published.
[Collection(BrokerCollection.Name)]
public sealed class G2_RabbitMq_MensagemInexprimivelEmAmqp_VaiParaDlq(PostgresFixture postgres, RabbitMqFixture rabbit)
{
    [Fact]
    public async Task G2_RabbitMq_MensagemInexprimivelEmAmqp_VaiParaDlqEORestoPublica()
    {
        var ct = TestContext.Current.CancellationToken;
        var database = await TestDatabase.CreateAsync(postgres);
        var (exchange, queue) = await rabbit.DeclareTopologyAsync();
        var ids = await DispatcherHarness.EnqueueAsync(database, 3);
        var longCorrelation = new string('c', 300);
        await database.ScalarAsync(
            $"WITH u AS (UPDATE waybill.outbox SET headers = jsonb_build_object('correlation_id', '{longCorrelation}') WHERE id = '{ids[1]}' RETURNING 1) SELECT count(*) FROM u");
        await using var dataSource = NpgsqlDataSource.Create(database.ConnectionString);
        await using var transport = G2_RabbitMq_PublicaComConfirmacaoEPropriedades.Transport(rabbit, exchange);
        var dispatcher = DispatcherHarness.Create(dataSource, transport, DispatcherHarness.Options(database));

        await dispatcher.RunOnceAsync(ct);

        Assert.Equal(1, await database.ScalarAsync(
            $"SELECT count(*) FROM waybill.outbox WHERE id = '{ids[1]}' AND status = 'dlq' AND dlq_reason LIKE 'cannot be expressed as an AMQP message%'"));
        Assert.Equal(2, await database.CountAsync("published"));
        Assert.Equal(2, (await rabbit.DrainAsync(queue)).Count);
    }
}
