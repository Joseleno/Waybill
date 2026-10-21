using System.Text;
using Microsoft.Extensions.DependencyInjection;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using RabbitMQ.Client.Exceptions;
using Waybill.EntityFrameworkCore;
using Waybill.RabbitMQ;
using Waybill.Tests.Integration.G3;
using Waybill.Tests.Integration.G4;

namespace Waybill.Tests.Integration.G2.RabbitMq;

// G4 on the consumer, on a real broker with the sample's queues (stage 10): a consumer as an application would write
// it reads the order from the headers, runs the inbox with sequence verification, and sends a regression to the
// dead-letter exchange with basic.nack without requeue. The second test proves the queue dead-letters at least once:
// with the dead queue full, the refused dead letter stays held in the source queue instead of being dropped, which
// at-most-once (the fallback without reject-publish on the source queue) would do, silently. The broker retries it later:
// on 4.3.6, not within a minute of making room, so the test stops at the hold.
[Collection(BrokerCollection.Name)]
public sealed class G4_RabbitMq_RegressaoChegaADlx(PostgresFixture postgres, RabbitMqFixture rabbit)
{
    private const string RoutingKey = "billing.invoice-paid.v1";

    [Fact]
    public async Task G4_RabbitMq_CopiaTardiaDepoisDaRetencao_VaiParaAFilaDeMortas()
    {
        var ct = TestContext.Current.CancellationToken;
        var database = await TestDatabase.CreateAsync(postgres);
        await using var services = database.Services();
        var names = await rabbit.DeclareSampleTopologyAsync();
        var (exchange, queue, dead) = (names["events"], names["receipts.invoice-paid"], names["receipts.invoice-paid.dead"]);
        await using var connection = await new ConnectionFactory { Uri = rabbit.Uri }.CreateConnectionAsync(ct);
        var (first, second) = (Guid.CreateVersion7(), Guid.CreateVersion7());

        await PublishAsync(connection, exchange, (first, 1), (second, 2));
        Assert.All(await ConsumeAsync(connection, queue, services, 2, ct), r => Assert.Equal(OrderedInboxResult.Processed, r.Result));
        await InboxOrderHarness.ForgetInboxRecordsAsync(database);
        await PublishAsync(connection, exchange, (first, 1)); // a late copy

        Assert.Equal((first, OrderedInboxResult.Regressed), Assert.Single(await ConsumeAsync(connection, queue, services, 1, ct)));
        // Dead-lettering is asynchronous: the dead letter reaches the dead queue shortly after the nack.
        await UntilAsync(async () => (await rabbit.QueueCountsAsync(dead)).Messages == 1, "the regression never reached the dead queue", ct);
        var deadLetter = Assert.Single(await rabbit.DrainAsync(dead));
        Assert.Equal(first.ToString(), deadLetter.BasicProperties.MessageId);
        Assert.Equal("rejected", DeathReason(deadLetter.BasicProperties));
        Assert.Equal(2, await database.EffectsAsync());
    }

    [Fact]
    public async Task G4_RabbitMq_FilaDeMortasCheia_RegressaoFicaRetidaNaOrigem()
    {
        var ct = TestContext.Current.CancellationToken;
        var database = await TestDatabase.CreateAsync(postgres);
        await using var services = database.Services();
        var names = await rabbit.DeclareSampleTopologyAsync();
        var (exchange, queue, dead) = (names["events"], names["receipts.invoice-paid"], names["receipts.invoice-paid.dead"]);
        await using var connection = await new ConnectionFactory { Uri = rabbit.Uri }.CreateConnectionAsync(ct);
        await FillAsync(connection, dead, ct);
        var (first, second) = (Guid.CreateVersion7(), Guid.CreateVersion7());
        await PublishAsync(connection, exchange, (first, 1), (second, 2));
        await ConsumeAsync(connection, queue, services, 2, ct);
        await InboxOrderHarness.ForgetInboxRecordsAsync(database);
        await PublishAsync(connection, exchange, (first, 1));

        Assert.Equal(OrderedInboxResult.Regressed, Assert.Single(await ConsumeAsync(connection, queue, services, 1, ct)).Result);

        // Held: counted in the queue, neither ready nor unacknowledged; and still there a moment later, not dropped.
        await UntilAsync(async () => await rabbit.QueueCountsAsync(queue) == (1, 0, 0), "the refused dead letter was not held in the source queue", ct);
        await Task.Delay(TimeSpan.FromSeconds(3), ct);
        Assert.Equal((1L, 0L, 0L), await rabbit.QueueCountsAsync(queue));
    }

    // Ordered messages of one key, as the transport writes them.
    private static async Task PublishAsync(IConnection connection, string exchange, params (Guid Id, long Sequence)[] messages)
    {
        await using var channel = await connection.CreateChannelAsync(
            new CreateChannelOptions(publisherConfirmationsEnabled: true, publisherConfirmationTrackingEnabled: true), TestContext.Current.CancellationToken);
        foreach (var (id, sequence) in messages)
        {
            var properties = new BasicProperties
            {
                MessageId = id.ToString(),
                Persistent = true,
                Headers = new Dictionary<string, object?> { ["waybill-key"] = InboxOrderHarness.Key, ["waybill-sequence"] = sequence.ToString(System.Globalization.CultureInfo.InvariantCulture) },
            };
            await channel.BasicPublishAsync(exchange, RoutingKey, mandatory: true, properties, Encoding.UTF8.GetBytes("{}"), TestContext.Current.CancellationToken);
        }
    }

    // The consumer an application would write: one delivery at a time; a regression to the dead-letter exchange.
    private static async Task<List<(Guid Id, OrderedInboxResult Result)>> ConsumeAsync(
        IConnection connection, string queue, IServiceProvider services, int count, CancellationToken ct)
    {
        await using var channel = await connection.CreateChannelAsync(cancellationToken: ct);
        await channel.BasicQosAsync(prefetchSize: 0, prefetchCount: 1, global: false, ct);
        var results = new List<(Guid, OrderedInboxResult)>();
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var consumer = new AsyncEventingBasicConsumer(channel);
        consumer.ReceivedAsync += async (_, delivery) =>
        {
            var messageId = delivery.BasicProperties.GetWaybillMessageId();
            await using var scope = services.CreateAsyncScope();
            var result = await scope.ServiceProvider.GetRequiredService<IInbox<AppDbContext>>().ProcessInOrderAsync(
                InboxOrderHarness.Handler, messageId, delivery.BasicProperties.GetWaybillOrder(), InboxHarness.ApplyEffect);
            if (result == OrderedInboxResult.Regressed)
                await channel.BasicNackAsync(delivery.DeliveryTag, multiple: false, requeue: false);
            else
                await channel.BasicAckAsync(delivery.DeliveryTag, multiple: false);
            results.Add((messageId, result));
            if (results.Count == count)
                done.TrySetResult();
        };
        await channel.BasicConsumeAsync(queue, autoAck: false, consumer, ct);
        await done.Task.WaitAsync(TimeSpan.FromSeconds(30), ct);
        return results;
    }

    // Fills the dead queue to its x-max-length (10000 in the sample), until the broker refuses a confirmed publish.
    private static async Task FillAsync(IConnection connection, string dead, CancellationToken ct)
    {
        await using (var bulk = await connection.CreateChannelAsync(cancellationToken: ct))
        {
            for (var i = 0; i < 10_000; i++)
                await bulk.BasicPublishAsync(string.Empty, dead, Encoding.UTF8.GetBytes("filler"), ct);
        }
        await using var confirmed = await connection.CreateChannelAsync(
            new CreateChannelOptions(publisherConfirmationsEnabled: true, publisherConfirmationTrackingEnabled: true), ct);
        for (var i = 0; ; i++)
        {
            try
            {
                await confirmed.BasicPublishAsync(string.Empty, dead, Encoding.UTF8.GetBytes("filler"), ct);
            }
            catch (PublishException)
            {
                return; // nacked: full
            }
            Assert.True(i < 1_000, "the dead queue never refused a publish");
        }
    }

    private static async Task UntilAsync(Func<Task<bool>> condition, string failure, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (!await condition())
        {
            Assert.True(DateTime.UtcNow < deadline, failure);
            await Task.Delay(100, ct);
        }
    }

    // The reason of the latest x-death entry: "rejected" for a basic.nack without requeue.
    private static string? DeathReason(IReadOnlyBasicProperties properties) =>
        properties.Headers?["x-death"] is IList<object> { Count: > 0 } deaths && deaths[0] is IDictionary<string, object?> death && death["reason"] is byte[] reason
            ? Encoding.UTF8.GetString(reason)
            : null;
}
