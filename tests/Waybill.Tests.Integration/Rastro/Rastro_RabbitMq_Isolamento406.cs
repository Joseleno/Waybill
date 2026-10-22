using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using RabbitMQ.Client;
using Waybill.EntityFrameworkCore;
using Waybill.Tests.Integration.G2;
using Waybill.Tests.Integration.G2.RabbitMq;

namespace Waybill.Tests.Integration.Rastro;

// The broker closes the channel over a message above its max_message_size, and the transport publishes the unconfirmed
// ones again, each alone. Still one send span per message: an isolated one carries the event waybill.isolated, and every
// publish of it, the isolated one included, stays in the message's trace.
[Collection(SmallMessagesCollection.Name)]
public sealed class Rastro_RabbitMq_Isolamento406(PostgresFixture postgres, SmallMessagesRabbitMqFixture rabbit)
{
    [Fact]
    public async Task Rastro_RabbitMq_Isolamento406_EventoNoSend()
    {
        var ct = TestContext.Current.CancellationToken;
        using var recorder = new TraceRecorder(WaybillDiagnostics.ActivitySourceName, RabbitMQActivitySource.PublisherSourceName);
        var database = await TestDatabase.CreateAsync(postgres);
        var (exchange, queue) = await DeclareTopologyAsync();
        Guid large;
        using (recorder.StartRoot())
        {
            await using var services = database.Services();
            await using var scope = services.CreateAsyncScope();
            var outbox = scope.ServiceProvider.GetRequiredService<IOutbox<AppDbContext>>();
            for (var i = 0; i < 42; i++)
                outbox.Enqueue(new InvoicePaid(Guid.NewGuid(), i));
            large = outbox.Enqueue(new AuditRecorded(new string('x', 6_000))); // above 4 KiB, below 64 KiB
            for (var i = 43; i < 100; i++)
                outbox.Enqueue(new InvoicePaid(Guid.NewGuid(), i));
            await scope.ServiceProvider.GetRequiredService<AppDbContext>().SaveChangesAsync(ct);
        }
        await using var dataSource = NpgsqlDataSource.Create(database.ConnectionString);
        await using var transport = new Waybill.RabbitMQ.RabbitMqTransport(
            Microsoft.Extensions.Options.Options.Create(new Waybill.RabbitMQ.WaybillRabbitMqOptions { Uri = rabbit.Uri, Exchange = exchange }),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<Waybill.RabbitMQ.RabbitMqTransport>.Instance);
        var dispatcher = DispatcherHarness.Create(dataSource, transport, DispatcherHarness.Options(database, o => o.BatchSize = 100));

        await dispatcher.RunOnceAsync(ct);

        var sends = recorder.Stopped.Where(a => a.Source.Name == WaybillDiagnostics.ActivitySourceName && a.OperationName.StartsWith("send", StringComparison.Ordinal)).ToList();
        Assert.Equal(100, sends.Count);
        var culprit = Assert.Single(sends, s => (string?)s.GetTagItem("messaging.message.id") == large.ToString());
        Assert.Equal("dlq", culprit.GetTagItem("waybill.send.outcome"));
        var isolated = sends.Where(s => s.Events.Any(e => e.Name == "waybill.isolated")).ToList();
        Assert.Contains(culprit, isolated);
        Assert.All(isolated.Where(s => s != culprit), s => Assert.Equal("published", s.GetTagItem("waybill.send.outcome")));
        // Every publish, the isolated ones included, hangs from its message's send span.
        var sendIds = sends.Select(s => s.SpanId).ToHashSet();
        var clients = recorder.Stopped.Where(a => a.Source.Name == RabbitMQActivitySource.PublisherSourceName).ToList();
        Assert.True(clients.Count >= 100 + isolated.Count - 1, $"{clients.Count} client spans for 100 messages and {isolated.Count} isolated");
        Assert.All(clients, c => Assert.Contains(c.ParentSpanId, sendIds));
        Assert.All(await DrainTraceParentsAsync(queue), traceParent =>
            Assert.Equal(recorder.TraceId, ActivityContext.Parse(traceParent, null).TraceId));
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

    private async Task<List<string>> DrainTraceParentsAsync(string queue)
    {
        await using var connection = await new ConnectionFactory { Uri = rabbit.Uri }.CreateConnectionAsync();
        await using var channel = await connection.CreateChannelAsync();
        var traceParents = new List<string>();
        while (await channel.BasicGetAsync(queue, autoAck: true) is { } message)
            traceParents.Add(Encoding.UTF8.GetString((byte[])message.BasicProperties.Headers!["traceparent"]!));
        return traceParents;
    }
}
