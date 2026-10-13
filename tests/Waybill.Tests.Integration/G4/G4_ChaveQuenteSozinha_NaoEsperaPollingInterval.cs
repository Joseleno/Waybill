using Microsoft.Extensions.Diagnostics.HealthChecks;
using Waybill.Tests.Integration.G2;
using Waybill.Tests.Integration.Observabilidade;

namespace Waybill.Tests.Integration.G4;

// Ordering takes one row per key per batch, so one busy key alone never fills a batch, and the loop only skips the
// polling interval after a full one: a key with 30 messages would take 30 intervals. A claimed row that has more of its
// key behind it sends the loop straight to the next cycle (ADR 0008).
[Collection(PostgresCollection.Name)]
public sealed class G4_ChaveQuenteSozinha_NaoEsperaPollingInterval(PostgresFixture postgres)
{
    [Fact]
    public async Task G4_ChaveQuenteSozinha_DrenaSemEsperarOIntervalo()
    {
        var ct = TestContext.Current.CancellationToken;
        var database = await TestDatabase.CreateAsync(postgres);
        var transport = new FakeTransport();
        await using var harness = new HealthHarness(database, transport, dispatcher: o =>
        {
            o.OrderByKey = true;
            o.Partitions = 4;
            o.PollingInterval = TimeSpan.FromSeconds(5);
        });
        await harness.Dispatcher.StartAsync(ct);
        await harness.WaitForAsync(HealthStatus.Healthy, ct);

        await DispatcherHarness.EnqueueAsync(database, 30, key: _ => "order-1");
        var deadline = DateTime.UtcNow.AddSeconds(15); // the first claim may wait one interval; 30 would take 150 s
        while (transport.Received.Count < 30 && DateTime.UtcNow < deadline)
            await Task.Delay(50, ct);
        await harness.Dispatcher.StopAsync(ct);

        Assert.Equal(30, transport.Received.Count);
        var amounts = transport.Received.Select(m => System.Text.Json.JsonDocument.Parse(m.Payload).RootElement.GetProperty("Amount").GetInt32()).ToList();
        Assert.Equal(Enumerable.Range(0, 30), amounts); // and in enqueue order
    }
}
