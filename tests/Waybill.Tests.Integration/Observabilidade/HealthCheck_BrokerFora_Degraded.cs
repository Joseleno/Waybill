using Microsoft.Extensions.Diagnostics.HealthChecks;
using Waybill.Tests.Integration.G2;

namespace Waybill.Tests.Integration.Observabilidade;

// The broker down is Degraded, not Unhealthy: the application can still write and enqueue, and an orchestrator that
// restarted it on Unhealthy would gain nothing. Once the broker is back the check returns to Healthy.
[Collection(PostgresCollection.Name)]
public sealed class HealthCheck_BrokerFora_Degraded(PostgresFixture postgres)
{
    [Fact]
    public async Task HealthCheck_BrokerFora_DegradedOutboxAceitaEVoltaAHealthy()
    {
        var ct = TestContext.Current.CancellationToken;
        var database = await TestDatabase.CreateAsync(postgres);
        await DispatcherHarness.EnqueueAsync(database, 3);
        var brokerUp = false;
        var transport = new FakeTransport((_, batch, _) => Task.FromResult<IReadOnlyList<PublishResult>>(batch
            .Select(_ => Volatile.Read(ref brokerUp) ? PublishResult.Confirmed : PublishResult.RetryAfter(TransportFailure.Connection, "broker down"))
            .ToList()));
        await using var harness = new HealthHarness(database, transport);

        await harness.Dispatcher.StartAsync(ct);
        var degraded = await harness.WaitForAsync(HealthStatus.Degraded, ct);
        await DispatcherHarness.EnqueueAsync(database, 2); // the outbox still accepts events

        Assert.Contains("broker", degraded.Description);
        Assert.Equal(5, await database.CountAsync("pending"));

        Volatile.Write(ref brokerUp, true);
        await harness.WaitForAsync(HealthStatus.Healthy, ct);
        await harness.Dispatcher.StopAsync(ct);
    }
}
