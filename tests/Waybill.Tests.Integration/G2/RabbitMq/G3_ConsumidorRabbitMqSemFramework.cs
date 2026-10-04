using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using Waybill.EntityFrameworkCore;
using Waybill.RabbitMQ;
using Waybill.Tests.Integration.G3;

namespace Waybill.Tests.Integration.G2.RabbitMq;


// The inbox needs no consumption framework: a plain RabbitMQ.Client consumer reads the Waybill message id, wraps its
// handler with the inbox and acks after the commit. The broker delivers the same message twice (a redelivery after
// a lost ack, for instance); the effect applies once and both deliveries are acked.
[Collection(BrokerCollection.Name)]
public sealed class G3_ConsumidorRabbitMqSemFramework(PostgresFixture postgres, RabbitMqFixture rabbit)
{
    [Fact]
    public async Task G3_ConsumidorRabbitMqSemFramework_EntregaDupla_AplicaUmaVezEFazAckDasDuas()
    {
        var ct = TestContext.Current.CancellationToken;
        var database = await TestDatabase.CreateAsync(postgres);
        var (exchange, queue) = await rabbit.DeclareTopologyAsync("billing.#");

        // Producer side: the event leaves through the outbox and the dispatcher.
        var ids = await DispatcherHarness.EnqueueAsync(database, 1);
        await using (var dataSource = NpgsqlDataSource.Create(database.ConnectionString))
        await using (var transport = G2_RabbitMq_PublicaComConfirmacaoEPropriedades.Transport(rabbit, exchange))
            await DispatcherHarness.Create(dataSource, transport, DispatcherHarness.Options(database)).RunOnceAsync(ct);

        // The same message once more, with the same message_id, as a redelivery would bring it.
        await using var connection = await new ConnectionFactory { Uri = rabbit.Uri }.CreateConnectionAsync(ct);
        await using var channel = await connection.CreateChannelAsync(cancellationToken: ct);
        var original = await channel.BasicGetAsync(queue, autoAck: false, ct);
        Assert.NotNull(original);
        await channel.BasicNackAsync(original.DeliveryTag, multiple: false, requeue: true, ct);
        await channel.BasicPublishAsync(exchange, original.RoutingKey, mandatory: true, new BasicProperties(original.BasicProperties), original.Body, ct);

        // Consumer side, as an application would write it.
        await using var services = database.Services();
        var results = new ConcurrentQueue<InboxResult>();
        var consumer = new AsyncEventingBasicConsumer(channel);
        consumer.ReceivedAsync += async (_, delivery) =>
        {
            var messageId = delivery.BasicProperties.GetWaybillMessageId();
            var paid = JsonSerializer.Deserialize(delivery.Body.Span, TestJson.Default.InvoicePaid)!;

            await using var scope = services.CreateAsyncScope();
            var inbox = scope.ServiceProvider.GetRequiredService<IInbox<AppDbContext>>();
            var result = await inbox.ProcessAsync("billing.mark-invoice-paid", messageId, (db, _) =>
            {
                db.Invoices.Add(new Invoice { Number = $"effect-{paid.InvoiceId:N}", Amount = paid.Amount });
                return Task.CompletedTask;
            });

            await channel.BasicAckAsync(delivery.DeliveryTag, multiple: false); // after the commit, whatever the result
            results.Enqueue(result);
        };
        await channel.BasicConsumeAsync(queue, autoAck: false, consumer, ct);

        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (results.Count < 2 && DateTime.UtcNow < deadline)
            await Task.Delay(100, ct);

        Assert.Equal([InboxResult.Processed, InboxResult.Duplicate], results.Order());
        Assert.Equal(1, await database.EffectsAsync());
        Assert.Equal(1, await database.ScalarAsync($"SELECT count(*) FROM waybill.inbox WHERE handler = 'billing.mark-invoice-paid' AND message_id = '{ids[0]}'"));
        Assert.Equal(0u, (await channel.QueueDeclarePassiveAsync(queue, ct)).MessageCount); // both acked
    }
}
