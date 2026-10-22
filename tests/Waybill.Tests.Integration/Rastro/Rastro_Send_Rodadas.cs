using System.Diagnostics;
using Microsoft.Extensions.Time.Testing;
using Npgsql;
using Waybill.Tests.Integration.G2;
using Waybill.Tests.Integration.G4;

namespace Waybill.Tests.Integration.Rastro;

// With ordering by key and M = 2, a batch goes out in rounds. A row a round holds back (its key stopped, the round cut by
// the deadline, the batch ended by a retry, the host stopping) was not sent, and opens no send span.
[Collection(PostgresCollection.Name)]
public sealed class Rastro_Send_Rodadas(PostgresFixture postgres)
{
    public static TheoryData<string> HeldBack => ["chave-parada", "rodada-cortada", "lote-encerrado", "desligamento"];

    [Theory]
    [MemberData(nameof(HeldBack))]
    public async Task Rastro_Send_LinhaNaoEnviada_SemSpan(string scenario)
    {
        var ct = TestContext.Current.CancellationToken;
        using var recorder = new TraceRecorder(WaybillDiagnostics.ActivitySourceName);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var time = new FakeTimeProvider();
        var database = await TestDatabase.CreateAsync(postgres);
        await using var dataSource = NpgsqlDataSource.Create(database.ConnectionString);
        var transport = new FakeTransport((call, batch, _) =>
        {
            if (call == 1 && scenario == "rodada-cortada")
                time.Advance(TimeSpan.FromSeconds(15)); // 5 s of 20 left: round 2 does not start
            if (call == 1 && scenario == "desligamento")
                stop.Cancel();
            return Task.FromResult<IReadOnlyList<PublishResult>>(batch.Select(m => scenario switch
            {
                "chave-parada" => PublishResult.Returned("312 NO_ROUTE"),
                "lote-encerrado" when m.Key == "K" => PublishResult.RetryAfter(TransportFailure.Nacked, "nacked"),
                _ => PublishResult.Confirmed,
            }).ToList());
        });
        var options = OrderingHarness.Options(database, partitions: 4, maxPerKey: 2);
        options.PartitionLease = TimeSpan.FromDays(1); // the fake clock jumps; the partitions must not expire in it
        var dispatcher = OrderingHarness.Create(dataSource, transport, options, time);
        Assert.Null(await dispatcher.CheckOrderingAsync(atStartup: true, ct));
        Assert.Equal(0, (await dispatcher.RunOnceAsync(ct)).Claimed); // takes the partitions
        string[] keys = scenario == "lote-encerrado" ? ["K", "K", "L", "L"] : ["K", "K"];
        using (recorder.StartRoot())
            await DispatcherHarness.EnqueueAsync(database, keys.Length, key: i => keys[i]);

        await dispatcher.RunOnceAsync(stop.Token);

        Assert.Equal(1, transport.Calls); // only round 1 went out
        var sends = recorder.Stopped.Where(a => a.OperationName.StartsWith("send", StringComparison.Ordinal)).ToList();
        Assert.Equal(keys.Distinct().Count(), sends.Count);
        Assert.All(sends, s => Assert.Equal(1L, s.GetTagItem("waybill.sequence")));
    }
}
