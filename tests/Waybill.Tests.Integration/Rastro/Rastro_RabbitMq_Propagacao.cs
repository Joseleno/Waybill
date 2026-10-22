using System.Diagnostics;
using System.Text;
using Npgsql;
using RabbitMQ.Client;
using Waybill.Tests.Integration.G2;
using Waybill.Tests.Integration.G2.RabbitMq;

namespace Waybill.Tests.Integration.Rastro;

// The trace context recorded at Enqueue reaches the consumer, whoever listens. RabbitMQ.Client 7 opens a publish span of
// its own when anything listens to its publisher source, with Activity.Current as parent, and writes that span's
// traceparent over the message's. The dispatcher runs without a Current (a hosted service), so before 0.2 the consumer
// then got a new trace, unrelated to the request that enqueued the message.
[Collection(BrokerCollection.Name)]
public sealed class Rastro_RabbitMq_Propagacao(PostgresFixture postgres, RabbitMqFixture rabbit)
{
    private const string MessageName = "billing.invoice-paid.v1";

    [Fact]
    public async Task Rastro_RabbitMq_SoFonteDoClienteOuvida_ConsumidorNoTrace()
    {
        using var recorder = new TraceRecorder(RabbitMQActivitySource.PublisherSourceName);
        recorder.OwnRootName($"publish {MessageName}"); // the client's span when it has no parent

        var received = await PublishUnderRootAsync(recorder);

        Assert.Equal(3, received.Count);
        Assert.All(received, traceParent => Assert.Equal(recorder.TraceId, Context(traceParent).TraceId));
        // Control: the client's spans were sampled, so the client did write its own traceparent into each message.
        Assert.Equal(3, recorder.Stopped.Count(s => s.Source.Name == RabbitMQActivitySource.PublisherSourceName));
    }

    [Fact]
    public async Task Rastro_RabbitMq_NinguemOuve_TraceparentDaRequisicao()
    {
        using var recorder = new TraceRecorder(); // listens to nothing: only its root

        var received = await PublishUnderRootAsync(recorder);

        Assert.Equal(3, received.Count);
        Assert.All(received, traceParent => Assert.Equal(recorder.Root.Id, traceParent));
    }

    // Enqueues three messages under the test's root, then publishes them from a flow without Activity.Current, as the
    // dispatcher's hosted service does, and returns the traceparent each one reached the queue with.
    private async Task<List<string>> PublishUnderRootAsync(TraceRecorder recorder)
    {
        var ct = TestContext.Current.CancellationToken;
        var database = await TestDatabase.CreateAsync(postgres);
        var (exchange, queue) = await rabbit.DeclareTopologyAsync("billing.#");
        using (recorder.StartRoot())
            await DispatcherHarness.EnqueueAsync(database, 3);
        Assert.Null(Activity.Current);

        await using var dataSource = NpgsqlDataSource.Create(database.ConnectionString);
        await using var transport = G2_RabbitMq_PublicaComConfirmacaoEPropriedades.Transport(rabbit, exchange);
        var dispatcher = DispatcherHarness.Create(dataSource, transport, DispatcherHarness.Options(database));
        Assert.Equal(3, (await dispatcher.RunOnceAsync(ct)).Claimed);

        return [.. (await rabbit.DrainAsync(queue)).Select(m => Encoding.UTF8.GetString((byte[])m.BasicProperties.Headers!["traceparent"]!))];
    }

    private static ActivityContext Context(string traceParent) =>
        ActivityContext.TryParse(traceParent, null, out var context) ? context : throw new FormatException(traceParent);
}
