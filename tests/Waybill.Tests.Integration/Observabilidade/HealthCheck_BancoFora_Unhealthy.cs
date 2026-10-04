using Microsoft.Extensions.Diagnostics.HealthChecks;
using Waybill.Tests.Integration.G2;

namespace Waybill.Tests.Integration.Observabilidade;

// The database unreachable for three cycles in a row is Unhealthy: nothing can be enqueued or published.
[Collection(PostgresCollection.Name)]
public sealed class HealthCheck_BancoFora_Unhealthy(PostgresFixture postgres)
{
    [Fact]
    public async Task HealthCheck_BancoFora_UnhealthyEVoltaQuandoOBancoVolta()
    {
        var ct = TestContext.Current.CancellationToken;
        var database = await TestDatabase.CreateAsync(postgres);
        await using var harness = new HealthHarness(database, new FakeTransport());

        await HealthHarness.BlockAsync(postgres, database, block: true);
        await harness.Dispatcher.StartAsync(ct);
        var unhealthy = await harness.WaitForAsync(HealthStatus.Unhealthy, ct);

        Assert.Contains("database", unhealthy.Description);

        await HealthHarness.BlockAsync(postgres, database, block: false);
        await harness.WaitForAsync(HealthStatus.Healthy, ct);
        await harness.Dispatcher.StopAsync(ct);
    }
}
