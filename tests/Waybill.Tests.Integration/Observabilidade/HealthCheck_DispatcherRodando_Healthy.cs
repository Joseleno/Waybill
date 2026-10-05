using Microsoft.Extensions.Diagnostics.HealthChecks;
using Waybill.Tests.Integration.G2;

namespace Waybill.Tests.Integration.Observabilidade;

// AddHealthChecks().AddWaybillDispatcherCheck(): with the broker confirming, the dispatcher reports Healthy and the entry
// carries the oldest_pending_age_seconds field (its value is proven by the Metrica_* scenarios).
[Collection(PostgresCollection.Name)]
public sealed class HealthCheck_DispatcherRodando_Healthy(PostgresFixture postgres)
{
    [Fact]
    public async Task HealthCheck_BrokerConfirmando_HealthyComOCampoDeIdade()
    {
        var ct = TestContext.Current.CancellationToken;
        var database = await TestDatabase.CreateAsync(postgres);
        await DispatcherHarness.EnqueueAsync(database, 5);
        await using var harness = new HealthHarness(database, new FakeTransport());

        await harness.Dispatcher.StartAsync(ct);
        using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            deadline.CancelAfter(TimeSpan.FromSeconds(30));
            while (await database.CountAsync("published") < 5)
                await Task.Delay(50, deadline.Token);
        }
        var entry = await harness.CheckAsync(ct);
        await harness.Dispatcher.StopAsync(ct);

        Assert.Equal(HealthStatus.Healthy, entry.Status);
        Assert.True(entry.Data.ContainsKey("oldest_pending_age_seconds"));
    }
}
