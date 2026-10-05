using Microsoft.Extensions.Diagnostics.HealthChecks;
using Waybill.Tests.Integration.G2;

namespace Waybill.Tests.Integration.Observabilidade;

// A process whose dispatcher loop is not running publishes nothing, whatever the broker and the database say.
[Collection(PostgresCollection.Name)]
public sealed class HealthCheck_LacoParado_Unhealthy(PostgresFixture postgres)
{
    [Fact]
    public async Task HealthCheck_AntesDePartirEDepoisDeParar_Unhealthy()
    {
        var ct = TestContext.Current.CancellationToken;
        var database = await TestDatabase.CreateAsync(postgres);
        await using var harness = new HealthHarness(database, new FakeTransport());

        Assert.Equal(HealthStatus.Unhealthy, (await harness.CheckAsync(ct)).Status);

        await harness.Dispatcher.StartAsync(ct);
        // Running as soon as StartAsync returns: since .NET 10 ExecuteAsync starts on its own task, a check right after
        // start must not read "not running" (review finding).
        Assert.NotEqual(HealthStatus.Unhealthy, (await harness.CheckAsync(ct)).Status);
        await harness.WaitForAsync(HealthStatus.Healthy, ct);
        await harness.Dispatcher.StopAsync(ct);
        var stopped = await harness.CheckAsync(ct);

        Assert.Equal(HealthStatus.Unhealthy, stopped.Status);
        Assert.Contains("not running", stopped.Description);
    }
}
