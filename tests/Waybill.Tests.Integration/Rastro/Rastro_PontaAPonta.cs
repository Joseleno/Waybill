using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using Waybill.EntityFrameworkCore;
using Waybill.RabbitMQ;
using Waybill.Tests.Integration.G2;
using Waybill.Tests.Integration.G2.RabbitMq;

namespace Waybill.Tests.Integration.Rastro;

// The scope's promise (stage 11): one trace from the request that wrote the message to the handler that applied it. The
// root stands for the request; the dispatcher publishes from a flow without Activity.Current, as its hosted service
// does; the consumer is a plain RabbitMQ.Client consumer, its channel opened outside any activity, with the inbox in
// ReceivedAsync. With Waybill's and the client's sources listened, every step hangs from the one before.
[Collection(BrokerCollection.Name)]
public sealed class Rastro_PontaAPonta(PostgresFixture postgres, RabbitMqFixture rabbit)
{
    [Fact]
    public async Task Rastro_PontaAPonta_RequisicaoAteHandler()
    {
        var ct = TestContext.Current.CancellationToken;
        using var recorder = new TraceRecorder(
            WaybillDiagnostics.ActivitySourceName, RabbitMQActivitySource.PublisherSourceName, RabbitMQActivitySource.SubscriberSourceName);
        var database = await TestDatabase.CreateAsync(postgres);
        var (exchange, queue) = await rabbit.DeclareTopologyAsync("billing.#");
        await using var services = database.Services();
        using (recorder.StartRoot())
        {
            await using var scope = services.CreateAsyncScope();
            var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            context.Invoices.Add(new Invoice { Number = "INV-1", Amount = 10m });
            scope.ServiceProvider.GetRequiredService<IOutbox<AppDbContext>>().Enqueue(new InvoicePaid(Guid.NewGuid(), 10m));
            await context.SaveChangesAsync(ct);
        }
        Assert.Null(Activity.Current);

        await using (var dataSource = NpgsqlDataSource.Create(database.ConnectionString))
        await using (var transport = G2_RabbitMq_PublicaComConfirmacaoEPropriedades.Transport(rabbit, exchange))
            await DispatcherHarness.Create(dataSource, transport, DispatcherHarness.Options(database)).RunOnceAsync(ct);

        await using var connection = await new ConnectionFactory { Uri = rabbit.Uri }.CreateConnectionAsync(ct);
        await using var channel = await connection.CreateChannelAsync(cancellationToken: ct);
        var processed = new TaskCompletionSource<InboxResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var consumer = new AsyncEventingBasicConsumer(channel);
        consumer.ReceivedAsync += async (_, delivery) =>
        {
            await using var scope = services.CreateAsyncScope();
            var inbox = scope.ServiceProvider.GetRequiredService<IInbox<AppDbContext>>();
            var result = await inbox.ProcessAsync("receipts.invoice-paid", delivery.BasicProperties.GetWaybillMessageId(), (_, _) => Task.CompletedTask);
            await channel.BasicAckAsync(delivery.DeliveryTag, multiple: false);
            processed.TrySetResult(result);
        };
        await channel.BasicConsumeAsync(queue, autoAck: false, consumer, ct);

        Assert.Equal(InboxResult.Processed, await processed.Task.WaitAsync(TimeSpan.FromSeconds(30), ct));
        var deadline = DateTime.UtcNow.AddSeconds(10); // the deliver span ends after the handler returns
        while (!recorder.Stopped.Any(a => a.Source.Name == RabbitMQActivitySource.SubscriberSourceName) && DateTime.UtcNow < deadline)
            await Task.Delay(50, ct);

        var spans = recorder.Stopped;
        var create = Assert.Single(spans, a => a.OperationName == "create");
        var send = Assert.Single(spans, a => a.Source.Name == WaybillDiagnostics.ActivitySourceName && a.OperationName.StartsWith("send", StringComparison.Ordinal));
        var publish = Assert.Single(spans, a => a.Source.Name == RabbitMQActivitySource.PublisherSourceName);
        var deliver = Assert.Single(spans, a => a.Source.Name == RabbitMQActivitySource.SubscriberSourceName);
        var process = Assert.Single(spans, a => a.OperationName == "process");
        Assert.All(new[] { create, send, publish, deliver, process }, s => Assert.Equal(recorder.TraceId, s.TraceId));
        Assert.Equal(recorder.Root.SpanId, create.ParentSpanId);
        Assert.Equal(create.SpanId, send.ParentSpanId);
        Assert.Equal(send.SpanId, publish.ParentSpanId);
        Assert.Equal(publish.SpanId, deliver.ParentSpanId);
        Assert.Equal(deliver.SpanId, process.ParentSpanId);
    }
}
