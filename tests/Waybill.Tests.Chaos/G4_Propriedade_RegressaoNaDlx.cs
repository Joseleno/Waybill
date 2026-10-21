using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using Waybill.EntityFrameworkCore;
using Waybill.EntityFrameworkCore.Dispatching;
using Waybill.RabbitMQ;
using Waybill.Tests.Chaos.Broker;
using Waybill.Tests.Integration;
using Waybill.Tests.Integration.G4;
using Waybill.Tests.Integration.Retencao;

namespace Waybill.Tests.Chaos;

// G4 on the consumer (stage 10), end to end on RabbitMQ with the sample's queues: no regression is applied, and every
// regression reaches the consumer's dead-letter queue. Producers commit keyed messages; ordering dispatchers publish
// them, and now and then a zombie publishes through a path that delays what it sends past its confirm timeout, then dies
// holding its partitions: their next owner publishes the key on, and the zombie's copies arrive late. Inbox retention
// runs at almost zero, so a late copy passes deduplication and must be refused by its sequence. One consumer, one
// delivery at a time (the required consumption), a regression nacked to the DLX. After the drain the test publishes
// copies of applied messages behind their key: each must come back a regression, a count known in advance, so the test
// never passes on zero regressions. Released messages and handler failures have their own tests.
[Collection(ChaosBrokerCollection.Name)]
public sealed class G4_Propriedade_RegressaoNaDlx(PostgresFixture postgres, ChaosBrokerFixture broker)
{
    private const int Keys = 8;
    private const string Name = "billing.invoice-paid.v1";
    private const int ZombieLatencyMs = 4000;

    [Fact]
    public Task G4_Propriedade_RegressaoNaDlx_Curto() => Run(TimeSpan.FromSeconds(20), requireNatural: false);

    [Fact]
    [Trait("Category", "Long")]
    public Task G4_Propriedade_RegressaoNaDlx_DezMinutos() => Run(TimeSpan.FromMinutes(10), requireNatural: true);

    private async Task Run(TimeSpan duration, bool requireNatural)
    {
        var ct = TestContext.Current.CancellationToken;
        var database = await TestDatabase.CreateAsync(postgres);
        var names = await broker.DeclareSampleTopologyAsync();
        var (exchange, queue, dead) = (names["events"], names["receipts.invoice-paid"], names["receipts.invoice-paid.dead"]);
        await using var services = database.Services();
        await using var dataSource = NpgsqlDataSource.Create(database.ConnectionString);
        var transports = new ConcurrentBag<RabbitMqTransport>();
        RabbitMqTransport Transport(Uri uri)
        {
            var transport = new RabbitMqTransport(Options.Create(new WaybillRabbitMqOptions { Uri = uri, Exchange = exchange }), NullLogger<RabbitMqTransport>.Instance);
            transports.Add(transport);
            return transport;
        }

        var options = OrderingHarness.Options(database, partitions: 4, partitionLease: TimeSpan.FromSeconds(2));
        options.BatchSize = 20;
        options.PublishTimeout = TimeSpan.FromMilliseconds(500);
        options.LeaseMargin = TimeSpan.FromMilliseconds(300); // row lease 800 ms, partition lease more than twice that
        Assert.Null(await OrderingHarness.Create(dataSource, Transport(broker.ProxiedUri), options).CheckOrderingAsync(atStartup: true, ct));

        // The consumer an application would write, logging every delivery in the order it handled them.
        var log = new ConcurrentQueue<(Guid Id, string Key, long Sequence, OrderedInboxResult Result)>();
        await using var consumerConnection = await new ConnectionFactory { Uri = broker.DirectUri }.CreateConnectionAsync(ct);
        await using var consumerChannel = await consumerConnection.CreateChannelAsync(cancellationToken: ct);
        await consumerChannel.BasicQosAsync(prefetchSize: 0, prefetchCount: 1, global: false, ct);
        var consumer = new AsyncEventingBasicConsumer(consumerChannel);
        consumer.ReceivedAsync += async (_, delivery) =>
        {
            var messageId = delivery.BasicProperties.GetWaybillMessageId();
            var order = delivery.BasicProperties.GetWaybillOrder()!.Value;
            await using var scope = services.CreateAsyncScope();
            var result = await scope.ServiceProvider.GetRequiredService<IInbox<AppDbContext>>().ProcessInOrderAsync(
                InboxOrderHarness.Handler, messageId, order, (db, _) =>
                {
                    db.Invoices.Add(new Invoice { Number = messageId.ToString(), Amount = 1m });
                    return Task.CompletedTask;
                });
            if (result == OrderedInboxResult.Regressed)
                await consumerChannel.BasicNackAsync(delivery.DeliveryTag, multiple: false, requeue: false);
            else
                await consumerChannel.BasicAckAsync(delivery.DeliveryTag, multiple: false);
            log.Enqueue((messageId, order.Key, order.Sequence, result));
        };
        await consumerChannel.BasicConsumeAsync(queue, autoAck: false, consumer, ct);

        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var cleaner = RetentionHarness.Create(dataSource, RetentionHarness.Options(database, o => o.InboxRetention = TimeSpan.FromMilliseconds(1)));
        var retention = Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                await cleaner.RunOnceAsync(CancellationToken.None);
                await Task.Delay(50, CancellationToken.None);
            }
        }, ct);
        var dispatchers = Enumerable.Range(0, 3).Select(_ => Task.Run(async () =>
        {
            var dispatcher = OrderingHarness.Create(dataSource, Transport(broker.ProxiedUri), options);
            while (!stop.IsCancellationRequested)
            {
                if ((await dispatcher.RunOnceAsync(CancellationToken.None)).Claimed == 0)
                    await Task.Delay(20, CancellationToken.None);
            }
        }, ct)).ToList();

        // Producers, for the duration.
        var clock = Stopwatch.StartNew();
        var producers = Enumerable.Range(0, 2).Select(p => Task.Run(async () =>
        {
            var random = new Random(p);
            while (clock.Elapsed < duration)
            {
                await using var scope = services.CreateAsyncScope();
                var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                var outbox = scope.ServiceProvider.GetRequiredService<IOutbox<AppDbContext>>();
                for (var i = random.Next(1, 4); i > 0; i--)
                    outbox.Enqueue(new InvoicePaid(Guid.NewGuid(), 1m), $"key-{random.Next(Keys)}");
                await context.SaveChangesAsync(CancellationToken.None);
                await Task.Delay(20, CancellationToken.None);
            }
        }, ct)).ToList();

        // Zombies, one at a time: two seconds of normal life, then its sends are held past its confirm timeout while it
        // keeps cycling, then it dies holding its partitions. Its path stays slow until its late copies are out.
        var zombies = 0;
        while (clock.Elapsed < duration)
        {
            zombies++;
            var zombie = OrderingHarness.Create(dataSource, Transport(broker.ZombieUri), options);
            await CycleAsync(zombie, TimeSpan.FromSeconds(2));
            await broker.AddZombieLatencyAsync(ZombieLatencyMs);
            await CycleAsync(zombie, TimeSpan.FromSeconds(1.5));
            await Task.Delay(ZombieLatencyMs + 1000, ct); // dead; its copies arrive meanwhile
            await broker.RemoveZombieLatencyAsync();
        }
        await Task.WhenAll(producers);

        // The drain: everything published, everything delivered and handled.
        await UntilAsync(async () => await database.ScalarAsync("SELECT count(*) FROM waybill.outbox WHERE status <> 'published'") == 0,
            "the outbox did not drain", TimeSpan.FromSeconds(120), ct);
        await QuietAsync(log, queue, ct);
        var natural = log.Count(e => e.Result == OrderedInboxResult.Regressed);

        // Copies of applied messages behind their key, published after the drain: each is a regression.
        var random = new Random(42);
        var injected = log.Where(e => e.Result == OrderedInboxResult.Processed)
            .GroupBy(e => e.Key)
            .SelectMany(g => g.Where(e => e.Sequence < g.Max(x => x.Sequence)).OrderBy(_ => random.Next()).Take(2))
            .ToList();
        Assert.NotEmpty(injected);
        var before = log.Count;
        await cleaner.RunOnceAsync(ct);
        await using (var publisher = await consumerConnection.CreateChannelAsync(
                         new CreateChannelOptions(publisherConfirmationsEnabled: true, publisherConfirmationTrackingEnabled: true), ct))
        {
            foreach (var copy in injected)
            {
                var properties = new BasicProperties
                {
                    MessageId = copy.Id.ToString(),
                    Type = Name,
                    Persistent = true,
                    Headers = new Dictionary<string, object?> { ["waybill-key"] = copy.Key, ["waybill-sequence"] = copy.Sequence.ToString(CultureInfo.InvariantCulture) },
                };
                await publisher.BasicPublishAsync(exchange, Name, mandatory: true, properties, Encoding.UTF8.GetBytes("{}"), ct);
            }
        }
        await UntilAsync(() => Task.FromResult(log.Count == before + injected.Count), "the injected copies were not all handled", TimeSpan.FromSeconds(60), ct);
        await QuietAsync(log, queue, ct);

        await stop.CancelAsync();
        await Task.WhenAll([retention, .. dispatchers]);
        foreach (var transport in transports)
            await transport.DisposeAsync();

        var entries = log.ToList();
        var processed = entries.Where(e => e.Result == OrderedInboxResult.Processed).ToList();
        var regressed = entries.Where(e => e.Result == OrderedInboxResult.Regressed).ToList();
        var line = $"{zombies} zombie(s); {entries.Count} deliveries: {processed.Count} applied, {entries.Count(e => e.Result == OrderedInboxResult.Duplicate)} duplicate, " +
            $"{natural} natural regression(s), {injected.Count} injected";
        TestContext.Current.TestOutputHelper?.WriteLine(line);
        TestContext.Current.SendDiagnosticMessage(line);

        // Never out of order: per key, applied sequences strictly increase.
        foreach (var key in processed.GroupBy(e => e.Key))
        {
            var sequences = key.Select(e => e.Sequence).ToList();
            Assert.True(sequences.Zip(sequences.Skip(1)).All(p => p.First < p.Second), $"{key.Key} applied out of order: {string.Join(", ", sequences)}");
        }
        // Every message applied once, as its committed effect shows.
        var effects = await EffectsAsync(database);
        Assert.Equal(processed.Count, effects.Count);
        Assert.Equal(processed.Select(e => e.Id.ToString()).Order(StringComparer.Ordinal), effects.Order(StringComparer.Ordinal));
        Assert.Equal((await OutboxIdsAsync(database)).Order(StringComparer.Ordinal), processed.Select(e => e.Id.ToString()).Order(StringComparer.Ordinal));
        // Every regression is in the dead-letter queue, and nothing else.
        await UntilAsync(async () => (await broker.QueueCountsAsync(queue)).Messages == 0, "dead letters still held in the source queue", TimeSpan.FromSeconds(30), ct);
        var deadLetters = await broker.DrainAsync(dead);
        Assert.Equal(regressed.Select(e => e.Id.ToString()).Order(StringComparer.Ordinal), deadLetters.Select(m => m.BasicProperties.MessageId!).Order(StringComparer.Ordinal));
        // Every injected copy came back a regression.
        Assert.All(entries.Skip(before), e => Assert.Equal(OrderedInboxResult.Regressed, e.Result));
        Assert.Equal(injected.Select(e => e.Id).Order(), entries.Skip(before).Select(e => e.Id).Order());
        // The key ends at the last applied message.
        foreach (var key in processed.GroupBy(e => e.Key))
            Assert.Equal(key.Max(e => e.Sequence), (await InboxOrderHarness.LastAppliedAsync(database, key.Key))!.Value.Sequence);
        if (requireNatural)
            Assert.True(natural > 0, $"no zombie copy came back a regression: {line}");

        async Task CycleAsync(OutboxDispatcher zombie, TimeSpan time)
        {
            var lifetime = Stopwatch.StartNew();
            while (lifetime.Elapsed < time)
            {
                if ((await zombie.RunOnceAsync(CancellationToken.None)).Claimed == 0)
                    await Task.Delay(20, CancellationToken.None);
            }
        }
    }

    // No delivery for two seconds and nothing in the queue: ready, unacknowledged or a held dead letter.
    private async Task QuietAsync(ConcurrentQueue<(Guid, string, long, OrderedInboxResult)> log, string queue, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow.AddSeconds(120);
        var (seen, since) = (-1, Stopwatch.StartNew());
        while (true)
        {
            if (log.Count != seen)
                (seen, since) = (log.Count, Stopwatch.StartNew());
            else if (since.Elapsed > TimeSpan.FromSeconds(2) && await broker.QueueCountsAsync(queue) == (0, 0, 0))
                return;
            Assert.True(DateTime.UtcNow < deadline, "the consumer never went quiet");
            await Task.Delay(100, ct);
        }
    }

    private static async Task UntilAsync(Func<Task<bool>> condition, string failure, TimeSpan timeout, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!await condition())
        {
            Assert.True(DateTime.UtcNow < deadline, failure);
            await Task.Delay(100, ct);
        }
    }

    private static Task<List<string>> EffectsAsync(TestDatabase database) => StringsAsync(database, """SELECT "Number" FROM invoices""");

    private static Task<List<string>> OutboxIdsAsync(TestDatabase database) => StringsAsync(database, "SELECT id::text FROM waybill.outbox");

    private static async Task<List<string>> StringsAsync(TestDatabase database, string sql)
    {
        await using var connection = new NpgsqlConnection(database.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync();
        var values = new List<string>();
        while (await reader.ReadAsync())
            values.Add(reader.GetString(0));
        return values;
    }
}
