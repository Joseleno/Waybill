using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using RabbitMQ.Client;
using Waybill.RabbitMQ;

namespace Waybill.Tests.Integration.G2.RabbitMq;

[Collection(BrokerCollection.Name)]
public sealed class G2_RabbitMq_PublicaComConfirmacaoEPropriedades(PostgresFixture postgres, RabbitMqFixture rabbit)
{
    internal static RabbitMqTransport Transport(RabbitMqFixture rabbit, string exchange) =>
        new(Options.Create(new WaybillRabbitMqOptions { Uri = rabbit.Uri, Exchange = exchange }), NullLogger<RabbitMqTransport>.Instance);

    [Fact]
    public async Task G2_RabbitMq_PublicaComConfirmacaoEPropriedades_NaExchangeComONomeComoRoutingKey()
    {
        var ct = TestContext.Current.CancellationToken;
        var database = await TestDatabase.CreateAsync(postgres);
        var (exchange, queue) = await rabbit.DeclareTopologyAsync("billing.#");
        var ids = await DispatcherHarness.EnqueueAsync(database, 3);
        await using var dataSource = NpgsqlDataSource.Create(database.ConnectionString);
        await using var transport = Transport(rabbit, exchange);
        var dispatcher = DispatcherHarness.Create(dataSource, transport, DispatcherHarness.Options(database));

        await dispatcher.RunOnceAsync(ct);

        Assert.Equal(3, await database.CountAsync("published"));
        var delivered = await rabbit.DrainAsync(queue);
        Assert.Equal(ids.Order().Select(id => id.ToString()), delivered.Select(m => m.BasicProperties.MessageId));

        var message = delivered.Single(m => m.BasicProperties.MessageId == ids[0].ToString());
        Assert.Equal("billing.invoice-paid.v1", message.RoutingKey);
        Assert.Equal("billing.invoice-paid.v1", message.BasicProperties.Type);
        Assert.Equal("application/json", message.BasicProperties.ContentType);
        Assert.Equal(DeliveryModes.Persistent, message.BasicProperties.DeliveryMode);
        Assert.Equal("corr-0", message.BasicProperties.CorrelationId);
        Assert.Equal("invoice-0", Encoding.UTF8.GetString((byte[])message.BasicProperties.Headers!["waybill-key"]!));
        Assert.True(message.BasicProperties.Timestamp.UnixTime > 0);
        Assert.Equal(0m, JsonSerializer.Deserialize(message.Body.Span, TestJson.Default.InvoicePaid)!.Amount);
    }
}
