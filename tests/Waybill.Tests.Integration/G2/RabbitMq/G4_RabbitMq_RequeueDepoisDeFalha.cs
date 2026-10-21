using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace Waybill.Tests.Integration.G2.RabbitMq;

// The consumer's failure policy (stage 10 SPEC, "Falha do handler") rests on two things quorum queues do, measured on
// RabbitMQ 4.3.6 while planning: a delivery nacked with requeue comes back before the next message of the queue, so
// retrying it keeps its key in order; and that nack does not count toward x-delivery-limit, only a delivery handed back
// by a closed channel does, so a message that always fails and is requeued circles for ever. The image floats
// (rabbitmq:4-alpine): this test says so when a broker changes either. The queue is the sample's, from its definitions.
[Collection(BrokerCollection.Name)]
public sealed class G4_RabbitMq_RequeueDepoisDeFalha(RabbitMqFixture rabbit)
{
    private const string RoutingKey = "billing.invoice-paid.v1";

    [Fact]
    public async Task G4_RabbitMq_NackComRequeue_VoltaAntesDaSeguinteSemContarEntrega()
    {
        var ct = TestContext.Current.CancellationToken;
        var names = await rabbit.DeclareSampleTopologyAsync();
        var (exchange, queue) = (names["events"], names["receipts.invoice-paid"]);
        await using var connection = await new ConnectionFactory { Uri = rabbit.Uri }.CreateConnectionAsync(ct);
        await PublishAsync(connection, exchange, ct, "m1", "m2");

        // m1 nacked with requeue ten times, then acked; m2 acked.
        var deliveries = new List<(string Id, long Count)>();
        await using (var channel = await connection.CreateChannelAsync(cancellationToken: ct))
        {
            await channel.BasicQosAsync(prefetchSize: 0, prefetchCount: 1, global: false, ct);
            var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var consumer = new AsyncEventingBasicConsumer(channel);
            consumer.ReceivedAsync += async (_, delivery) =>
            {
                deliveries.Add((delivery.BasicProperties.MessageId!, DeliveryCount(delivery.BasicProperties)));
                if (delivery.BasicProperties.MessageId == "m1" && deliveries.Count <= 10)
                    await channel.BasicNackAsync(delivery.DeliveryTag, multiple: false, requeue: true);
                else
                    await channel.BasicAckAsync(delivery.DeliveryTag, multiple: false);
                if (deliveries.Count == 12)
                    done.TrySetResult();
            };
            await channel.BasicConsumeAsync(queue, autoAck: false, consumer, ct);
            await done.Task.WaitAsync(TimeSpan.FromSeconds(30), ct);
        }

        Assert.Equal([.. Enumerable.Repeat("m1", 11), "m2"], deliveries.Select(d => d.Id));
        Assert.All(deliveries, d => Assert.Equal(0, d.Count));
    }

    [Fact]
    public async Task G4_RabbitMq_CanalFechadoComEntregaPendente_ContaEntrega()
    {
        var ct = TestContext.Current.CancellationToken;
        var names = await rabbit.DeclareSampleTopologyAsync();
        var (exchange, queue) = (names["events"], names["receipts.invoice-paid"]);
        await using var connection = await new ConnectionFactory { Uri = rabbit.Uri }.CreateConnectionAsync(ct);
        await PublishAsync(connection, exchange, ct, "m1");

        // Delivered and never acknowledged: closing the channel hands it back, as a crashed consumer would. The second
        // channel acknowledges it. (A single-active-consumer quorum queue refuses basic.get, so both consume.)
        var first = await ConsumeOneAsync(connection, queue, ack: false, ct);
        var second = await ConsumeOneAsync(connection, queue, ack: true, ct);

        Assert.Equal(("m1", 0L), first);
        Assert.Equal(("m1", 1L), second);
    }

    private static async Task<(string MessageId, long Count)> ConsumeOneAsync(IConnection connection, string queue, bool ack, CancellationToken ct)
    {
        await using var channel = await connection.CreateChannelAsync(cancellationToken: ct);
        await channel.BasicQosAsync(prefetchSize: 0, prefetchCount: 1, global: false, ct);
        var delivered = new TaskCompletionSource<(string, long)>(TaskCreationOptions.RunContinuationsAsynchronously);
        var consumer = new AsyncEventingBasicConsumer(channel);
        consumer.ReceivedAsync += async (_, delivery) =>
        {
            if (ack)
                await channel.BasicAckAsync(delivery.DeliveryTag, multiple: false);
            delivered.TrySetResult((delivery.BasicProperties.MessageId!, DeliveryCount(delivery.BasicProperties)));
        };
        await channel.BasicConsumeAsync(queue, autoAck: false, consumer, ct);
        return await delivered.Task.WaitAsync(TimeSpan.FromSeconds(30), ct);
    }

    private static async Task PublishAsync(IConnection connection, string exchange, CancellationToken ct, params string[] messageIds)
    {
        await using var channel = await connection.CreateChannelAsync(
            new CreateChannelOptions(publisherConfirmationsEnabled: true, publisherConfirmationTrackingEnabled: true), ct);
        foreach (var messageId in messageIds)
            await channel.BasicPublishAsync(exchange, RoutingKey, mandatory: true, new BasicProperties { MessageId = messageId, Persistent = true }, ReadOnlyMemory<byte>.Empty, ct);
    }

    // Quorum queues count returned deliveries in x-delivery-count; absent until the first one.
    private static long DeliveryCount(IReadOnlyBasicProperties properties) =>
        properties.Headers?.TryGetValue("x-delivery-count", out var count) == true ? Convert.ToInt64(count) : 0;
}
