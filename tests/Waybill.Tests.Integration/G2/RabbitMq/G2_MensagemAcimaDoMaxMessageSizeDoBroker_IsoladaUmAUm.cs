using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using RabbitMQ.Client;
using Testcontainers.RabbitMq;
using Waybill.EntityFrameworkCore;

namespace Waybill.Tests.Integration.G2.RabbitMq;

/// <summary>A broker whose max_message_size (4 KiB) is below Waybill's MaxPayloadBytes (64 KiB).</summary>
public sealed class SmallMessagesRabbitMqFixture : IAsyncLifetime
{
    private readonly RabbitMqContainer _container = new RabbitMqBuilder("rabbitmq:4-alpine")
        .WithResourceMapping(Encoding.UTF8.GetBytes("max_message_size = 4096\n"), "/etc/rabbitmq/conf.d/90-waybill.conf")
        .Build();

    public Uri Uri => new(_container.GetConnectionString());

    public ValueTask InitializeAsync() => new(_container.StartAsync());

    public ValueTask DisposeAsync() => _container.DisposeAsync();
}

[CollectionDefinition(Name)]
public sealed class SmallMessagesCollection : ICollectionFixture<PostgresFixture>, ICollectionFixture<SmallMessagesRabbitMqFixture>
{
    public const string Name = "postgres-rabbitmq-small-messages";
}

// G2: a message between the local limit and the broker's max_message_size makes the broker close the channel with
// the batch in flight, and the client cannot say which message did it. The batch is published again one by one on a
// fresh channel: only that message goes to the DLQ; the other 99 are published.
[Collection(SmallMessagesCollection.Name)]
public sealed class G2_MensagemAcimaDoMaxMessageSizeDoBroker_IsoladaUmAUm(PostgresFixture postgres, SmallMessagesRabbitMqFixture rabbit)
{
    [Fact]
    public async Task G2_MensagemAcimaDoMaxMessageSizeDoBroker_IsoladaUmAUm_SoElaVaiParaDlq()
    {
        var ct = TestContext.Current.CancellationToken;
        var database = await TestDatabase.CreateAsync(postgres);
        var (exchange, queue) = await DeclareTopologyAsync();

        Guid large;
        await using (var services = database.Services())
        await using (var scope = services.CreateAsyncScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var outbox = scope.ServiceProvider.GetRequiredService<IOutbox<AppDbContext>>();
            for (var i = 0; i < 42; i++)
                outbox.Enqueue(new InvoicePaid(Guid.NewGuid(), i));
            large = outbox.Enqueue(new AuditRecorded(new string('x', 6_000))); // above 4 KiB, below 64 KiB
            for (var i = 43; i < 100; i++)
                outbox.Enqueue(new InvoicePaid(Guid.NewGuid(), i));
            await context.SaveChangesAsync(ct);
        }

        await using var dataSource = NpgsqlDataSource.Create(database.ConnectionString);
        await using var transport = new Waybill.RabbitMQ.RabbitMqTransport(
            Microsoft.Extensions.Options.Options.Create(new Waybill.RabbitMQ.WaybillRabbitMqOptions { Uri = rabbit.Uri, Exchange = exchange }),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<Waybill.RabbitMQ.RabbitMqTransport>.Instance);
        var dispatcher = DispatcherHarness.Create(dataSource, transport, DispatcherHarness.Options(database, o => o.BatchSize = 100));

        await dispatcher.RunOnceAsync(ct);

        Assert.Equal(1, await database.ScalarAsync(
            $"SELECT count(*) FROM waybill.outbox WHERE id = '{large}' AND status = 'dlq' AND dlq_reason LIKE '%closed the channel when this message was published alone%406%'"));
        Assert.Equal(99, await database.CountAsync("published"));
        Assert.Equal(0, await database.ScalarAsync("SELECT coalesce(max(attempts), 0) FROM waybill.outbox WHERE status = 'published'"));
        Assert.True(await CountQueueAsync(queue) >= 99); // at least once: the ones confirmed before the close may come twice
    }

    private async Task<(string Exchange, string Queue)> DeclareTopologyAsync()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        await using var connection = await new ConnectionFactory { Uri = rabbit.Uri }.CreateConnectionAsync();
        await using var channel = await connection.CreateChannelAsync();
        await channel.ExchangeDeclareAsync($"events-{suffix}", ExchangeType.Topic, durable: true);
        await channel.QueueDeclareAsync($"all-{suffix}", durable: true, exclusive: false, autoDelete: false);
        await channel.QueueBindAsync($"all-{suffix}", $"events-{suffix}", "#");
        return ($"events-{suffix}", $"all-{suffix}");
    }

    private async Task<uint> CountQueueAsync(string queue)
    {
        await using var connection = await new ConnectionFactory { Uri = rabbit.Uri }.CreateConnectionAsync();
        await using var channel = await connection.CreateChannelAsync();
        return (await channel.QueueDeclarePassiveAsync(queue)).MessageCount;
    }
}
