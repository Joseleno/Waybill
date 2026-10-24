using Npgsql;
using RabbitMQ.Client;
using Waybill.Tests.Integration.G2;
using Waybill.Tests.Integration.G2.RabbitMq;
using Waybill.Tests.Integration.G4;

namespace Waybill.Tests.Integration.Convivencia;

// Services upgrade one at a time, so a consumer still on 0.1 receives what a producer on 0.2 publishes, here with
// ordering on, so every message carries waybill-sequence, a header 0.1 never wrote. The 0.1 consumer reads the message
// id as before, and its inbox absorbs a copy.
[Collection(BrokerCollection.Name)]
public sealed class Convivencia_Consumidor01(PostgresFixture postgres, RabbitMqFixture rabbit)
{
    [Fact]
    public async Task Convivencia_Consumidor01_Recebe02EDeduplica()
    {
        var ct = TestContext.Current.CancellationToken;
        var database = await TestDatabase.CreateAsync(postgres);
        var (exchange, queue) = await rabbit.DeclareTopologyAsync("billing.#");
        await using var dataSource = NpgsqlDataSource.Create(database.ConnectionString);
        await using var transport = G2_RabbitMq_PublicaComConfirmacaoEPropriedades.Transport(rabbit, exchange);
        var dispatcher = OrderingHarness.Create(dataSource, transport, OrderingHarness.Options(database, partitions: 4));
        Assert.Null(await dispatcher.CheckOrderingAsync(atStartup: true, ct));

        var ids = await DispatcherHarness.EnqueueAsync(database, 20, key: n => $"invoice-{n % 4}");
        for (var cycle = 0; cycle < 100 && await database.CountAsync("published") < 20; cycle++)
            await dispatcher.RunOnceAsync(ct);
        Assert.Equal(20, await database.CountAsync("published"));

        // One message once more, with the same properties, as a redelivery after a lost ack would bring it.
        Guid copy;
        await using (var connection = await new ConnectionFactory { Uri = rabbit.Uri }.CreateConnectionAsync(ct))
        await using (var channel = await connection.CreateChannelAsync(cancellationToken: ct))
        {
            var original = await channel.BasicGetAsync(queue, autoAck: false, ct);
            Assert.NotNull(original);
            Assert.True(original.BasicProperties.Headers?.ContainsKey("waybill-sequence"));
            copy = Guid.Parse(original.BasicProperties.MessageId!);
            await channel.BasicPublishAsync(exchange, original.RoutingKey, mandatory: true, new BasicProperties(original.BasicProperties), original.Body, ct);
            await channel.BasicNackAsync(original.DeliveryTag, multiple: false, requeue: true, ct);
        }

        await using var consumer = await LegacyHost.StartAsync("consume", database.ConnectionString, rabbit.Uri.ToString(), queue, "legacy.mark-paid");
        await consumer.WaitUntilAsync(h => h.Lines("PROCESSED ").Count + h.Lines("DUPLICATE ").Count == 21, TimeSpan.FromSeconds(60),
            "21 deliveries handled");
        await consumer.StopAsync();

        Assert.Equal(ids.Order(), consumer.Lines("PROCESSED ").Select(Guid.Parse).Order());
        Assert.Equal([copy], consumer.Lines("DUPLICATE ").Select(Guid.Parse));
        Assert.Equal(20, await database.ScalarAsync("SELECT count(*) FROM waybill.inbox WHERE handler = 'legacy.mark-paid'"));
    }
}
