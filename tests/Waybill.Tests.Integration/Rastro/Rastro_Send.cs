using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Waybill.EntityFrameworkCore;
using Waybill.Tests.Integration.G2;

namespace Waybill.Tests.Integration.Rastro;

// The dispatcher's send span (stage 11): one per message it hands to the transport or judges itself, child of the
// context recorded at Enqueue and linked to it, so every attempt, hours later too, lands in the trace of the request
// that wrote the message. It ends after the marking, with what the database recorded.
[Collection(PostgresCollection.Name)]
public sealed class Rastro_Send(PostgresFixture postgres)
{
    private static IEnumerable<Activity> Sends(TraceRecorder recorder) => recorder.Stopped.Where(a => a.OperationName.StartsWith("send", StringComparison.Ordinal));

    private async Task<(TestDatabase Database, NpgsqlDataSource DataSource, List<Guid> Ids)> EnqueueUnderRootAsync(
        TraceRecorder recorder, int count = 1, Func<int, string?>? key = null)
    {
        var database = await TestDatabase.CreateAsync(postgres);
        List<Guid> ids;
        using (recorder.StartRoot())
            ids = await DispatcherHarness.EnqueueAsync(database, count, key: key);
        Assert.Null(Activity.Current); // the dispatcher runs without one, as its hosted service does
        return (database, NpgsqlDataSource.Create(database.ConnectionString), ids);
    }

    [Fact]
    public async Task Rastro_Send_FilhoDoContextoGravado_ComLink()
    {
        var ct = TestContext.Current.CancellationToken;
        using var recorder = new TraceRecorder(WaybillDiagnostics.ActivitySourceName);
        var (database, dataSource, ids) = await EnqueueUnderRootAsync(recorder);
        await using var _ = dataSource;
        var transport = new FakeTransport();
        var dispatcher = DispatcherHarness.Create(dataSource, transport, DispatcherHarness.Options(database));

        await dispatcher.RunOnceAsync(ct);

        var create = Assert.Single(recorder.Stopped, a => a.OperationName == "create");
        var send = Assert.Single(Sends(recorder));
        Assert.Equal(("send", ActivityKind.Producer), (send.OperationName, send.Kind)); // FakeTransport names no destination
        Assert.Equal(create.SpanId, send.ParentSpanId);
        Assert.True(send.HasRemoteParent);
        Assert.Equal(create.SpanId, Assert.Single(send.Links).Context.SpanId);
        Assert.Equal("send", send.GetTagItem("messaging.operation.type"));
        Assert.Equal("send", send.GetTagItem("messaging.operation.name"));
        Assert.Equal(ids[0].ToString(), send.GetTagItem("messaging.message.id"));
        Assert.Equal("published", send.GetTagItem("waybill.send.outcome"));
        Assert.Equal(ActivityStatusCode.Unset, send.Status);
        Assert.Equal(send.Id, Assert.Single(transport.Received).Headers["traceparent"]); // what any transport propagates
        Assert.Null(Activity.Current);
    }

    [Fact]
    public async Task Rastro_Send_TracestatePreservado()
    {
        var ct = TestContext.Current.CancellationToken;
        using var recorder = new TraceRecorder(WaybillDiagnostics.ActivitySourceName);
        recorder.Root.TraceStateString = "vendor=1";
        var (database, dataSource, _) = await EnqueueUnderRootAsync(recorder);
        await using var source = dataSource;
        var dispatcher = DispatcherHarness.Create(dataSource, new FakeTransport(), DispatcherHarness.Options(database));

        await dispatcher.RunOnceAsync(ct);

        Assert.Equal("vendor=1", Assert.Single(Sends(recorder)).TraceStateString);
    }

    public static TheoryData<string> Outcomes =>
    [
        "confirmada", "conexao", "timeout-de-confirmacao", "nack", "devolvida", "max-returns", "defeito", "payload-acima",
        "fence-perdido", "timeout-do-lote", "marcacao-falha",
    ];

    // Each case ends the span once, with the outcome the marking recorded and an error type from a closed list.
    [Theory]
    [MemberData(nameof(Outcomes))]
    public async Task Rastro_Send_Desfechos(string scenario)
    {
        var ct = TestContext.Current.CancellationToken;
        using var recorder = new TraceRecorder(WaybillDiagnostics.ActivitySourceName);
        var (database, dataSource, ids) = await EnqueueUnderRootAsync(recorder);
        await using var _ = dataSource;
        var (expected, error, status) = scenario switch
        {
            "confirmada" => ("published", null, "published"),
            "conexao" => ("retry", "connection", "pending"),
            "timeout-de-confirmacao" => ("retry", "confirm_timeout", "pending"),
            "nack" => ("retry", "nacked", "pending"),
            "devolvida" => ("returned", "returned", "pending"),
            "max-returns" => ("dlq", "returned", "dlq"),
            "defeito" => ("dlq", "defect", "dlq"),
            "payload-acima" => ("dlq", "defect", "dlq"),
            "fence-perdido" => ("fenced", "fenced", "claimed"),
            "timeout-do-lote" => ("retry", "confirm_timeout", "pending"),
            _ => ("unknown", typeof(PostgresException).FullName, "claimed"),
        };
        var transport = new FakeTransport(async (_, batch, _) =>
        {
            switch (scenario)
            {
                case "fence-perdido":
                    await database.ExecuteAsync("UPDATE waybill.outbox SET fence = fence + 1");
                    break;
                case "timeout-do-lote":
                    await new TaskCompletionSource().Task; // never confirmed
                    break;
                case "marcacao-falha":
                    await database.ExecuteAsync("""
                        CREATE FUNCTION waybill.test_marking_fails() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN RAISE EXCEPTION 'marking fails'; END $$;
                        CREATE TRIGGER test_marking_fails BEFORE UPDATE ON waybill.outbox FOR EACH ROW EXECUTE FUNCTION waybill.test_marking_fails();
                        """);
                    break;
            }
            return batch.Select(_ => scenario switch
            {
                "conexao" => PublishResult.RetryAfter(TransportFailure.Connection, "closed"),
                "timeout-de-confirmacao" => PublishResult.RetryAfter(TransportFailure.ConfirmTimeout, "late"),
                "nack" => PublishResult.RetryAfter(TransportFailure.Nacked, "nacked"),
                "devolvida" or "max-returns" => PublishResult.Returned("312 NO_ROUTE"),
                "defeito" => PublishResult.Defect("refused"),
                _ => PublishResult.Confirmed,
            }).ToList();
        });
        var options = DispatcherHarness.Options(database, o =>
        {
            o.MaxReturns = scenario == "max-returns" ? 1 : 5;
            if (scenario == "timeout-do-lote")
                o.PublishTimeout = TimeSpan.FromSeconds(1);
        });
        var dispatcher = DispatcherHarness.Create(dataSource, transport, options, maxPayloadBytes: scenario == "payload-acima" ? 8 : 64 * 1024);

        if (scenario == "marcacao-falha")
            await Assert.ThrowsAsync<PostgresException>(() => dispatcher.RunOnceAsync(ct));
        else
            await dispatcher.RunOnceAsync(ct);

        var send = Assert.Single(Sends(recorder));
        Assert.Equal(expected, send.GetTagItem("waybill.send.outcome"));
        Assert.Equal(error, send.GetTagItem("error.type"));
        Assert.Equal(error is null ? ActivityStatusCode.Unset : ActivityStatusCode.Error, send.Status);
        Assert.Equal(scenario == "payload-acima" ? 0 : 1, transport.Calls);
        if (scenario == "marcacao-falha")
            await database.ExecuteAsync("DROP TRIGGER test_marking_fails ON waybill.outbox");
        Assert.Equal(1, await database.ScalarAsync($"SELECT count(*) FROM waybill.outbox WHERE id = '{ids[0]}' AND status = '{status}'"));
    }

    [Fact]
    public async Task Rastro_Send_LeaseVencido_MesmoTraceMesmoPai()
    {
        var ct = TestContext.Current.CancellationToken;
        using var recorder = new TraceRecorder(WaybillDiagnostics.ActivitySourceName);
        var (database, dataSource, _) = await EnqueueUnderRootAsync(recorder);
        await using var _ = dataSource;
        var successor = DispatcherHarness.Create(dataSource, new FakeTransport(), DispatcherHarness.Options(database));
        // The first attempt hangs past its lease: another dispatcher takes the row and publishes it meanwhile.
        var zombie = DispatcherHarness.Create(dataSource, new FakeTransport(async (_, batch, _) =>
        {
            await database.ExecuteAsync("UPDATE waybill.outbox SET lease_until = clock_timestamp() - interval '1 second'");
            await successor.RunOnceAsync(ct);
            return await FakeTransport.ConfirmAll(batch);
        }), DispatcherHarness.Options(database));

        await zombie.RunOnceAsync(ct);

        var sends = Sends(recorder).ToList();
        Assert.Equal(2, sends.Count);
        Assert.Single(sends.Select(s => (s.TraceId, s.ParentSpanId)).Distinct());
        Assert.Equal(["fenced", "published"], sends.Select(s => (string)s.GetTagItem("waybill.send.outcome")!).Order());
    }

    // Messages of different requests in one batch, and one written outside any trace, while the cycle runs under an
    // activity of its own (a host started inside a span): no send crosses traces, and the one without a recorded context
    // is a root, never a child of the ambient activity.
    [Fact]
    public async Task Rastro_Send_LoteMisto_NaoCruza()
    {
        var ct = TestContext.Current.CancellationToken;
        var database = await TestDatabase.CreateAsync(postgres);
        var recorders = Enumerable.Range(0, 3).Select(_ => new TraceRecorder(WaybillDiagnostics.ActivitySourceName)).ToList();
        try
        {
            foreach (var recorder in recorders)
            {
                using (recorder.StartRoot())
                    await DispatcherHarness.EnqueueAsync(database, 1);
            }
            var orphan = (await DispatcherHarness.EnqueueAsync(database, 1))[0];
            recorders[0].OwnMessage(orphan);
            await using var dataSource = NpgsqlDataSource.Create(database.ConnectionString);
            var dispatcher = DispatcherHarness.Create(dataSource, new FakeTransport(), DispatcherHarness.Options(database));
            var ambient = new Activity("host").SetIdFormat(ActivityIdFormat.W3C);
            ambient.ActivityTraceFlags = ActivityTraceFlags.Recorded;
            using (ambient.Start())
            {
                await dispatcher.RunOnceAsync(ct);
                Assert.Same(ambient, Activity.Current);
            }

            foreach (var recorder in recorders)
            {
                var create = Assert.Single(recorder.Stopped, a => a.OperationName == "create");
                var send = Assert.Single(Sends(recorder), s => s.TraceId == recorder.TraceId);
                Assert.Equal(create.SpanId, send.ParentSpanId);
            }
            var root = Assert.Single(Sends(recorders[0]), s => (string?)s.GetTagItem("messaging.message.id") == orphan.ToString());
            Assert.Equal(default, root.ParentSpanId);
            Assert.NotEqual(ambient.TraceId, root.TraceId);
            Assert.Empty(root.Links);
        }
        finally
        {
            recorders.ForEach(r => r.Dispose());
        }
    }

    [Fact]
    public async Task Rastro_Send_TraceparentMalformado_Raiz()
    {
        var ct = TestContext.Current.CancellationToken;
        using var recorder = new TraceRecorder(WaybillDiagnostics.ActivitySourceName);
        var database = await TestDatabase.CreateAsync(postgres);
        var id = (await DispatcherHarness.EnqueueAsync(database, 1))[0];
        await database.ExecuteAsync($$"""UPDATE waybill.outbox SET headers = '{"traceparent":"00-not-a-trace-01"}' WHERE id = '{{id}}'""");
        recorder.OwnMessage(id);
        await using var dataSource = NpgsqlDataSource.Create(database.ConnectionString);
        var transport = new FakeTransport();
        var dispatcher = DispatcherHarness.Create(dataSource, transport, DispatcherHarness.Options(database));

        await dispatcher.RunOnceAsync(ct);

        var send = Assert.Single(Sends(recorder));
        Assert.Equal(default, send.ParentSpanId);
        Assert.Empty(send.Links);
        Assert.Equal("published", send.GetTagItem("waybill.send.outcome"));
        Assert.Equal(send.Id, Assert.Single(transport.Received).Headers["traceparent"]);
    }

    [Fact]
    public async Task Rastro_Send_SemOuvinte_CabecalhoIntacto()
    {
        var ct = TestContext.Current.CancellationToken;
        using var recorder = new TraceRecorder(); // nothing listens to Waybill's source
        var (database, dataSource, _) = await EnqueueUnderRootAsync(recorder);
        await using var _ = dataSource;
        var transport = new FakeTransport();
        var dispatcher = DispatcherHarness.Create(dataSource, transport, DispatcherHarness.Options(database));

        await dispatcher.RunOnceAsync(ct);

        var received = Assert.Single(transport.Received);
        Assert.Equal(recorder.Root.Id, received.Headers["traceparent"]);
        Assert.Null(received.SendActivity);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Rastro_ChaveETenant_SoComAOpcao_NoSend(bool recorded)
    {
        var ct = TestContext.Current.CancellationToken;
        using var recorder = new TraceRecorder(WaybillDiagnostics.ActivitySourceName);
        var database = await TestDatabase.CreateAsync(postgres);
        using (recorder.StartRoot())
        {
            await using var services = database.Services();
            await using var scope = services.CreateAsyncScope();
            scope.ServiceProvider.GetRequiredService<IOutbox<AppDbContext>>().Enqueue(new InvoicePaid(Guid.NewGuid(), 1m), "invoice-1", tenantId: "tenant-1");
            await scope.ServiceProvider.GetRequiredService<AppDbContext>().SaveChangesAsync(ct);
        }
        await using var dataSource = NpgsqlDataSource.Create(database.ConnectionString);
        var dispatcher = DispatcherHarness.Create(dataSource, new FakeTransport(), DispatcherHarness.Options(database), recordKeyAndTenantInTraces: recorded);

        await dispatcher.RunOnceAsync(ct);

        var send = Assert.Single(Sends(recorder));
        Assert.Equal(recorded ? "invoice-1" : null, send.GetTagItem("waybill.key"));
        Assert.Equal(recorded ? "tenant-1" : null, send.GetTagItem("waybill.tenant_id"));
    }
}
