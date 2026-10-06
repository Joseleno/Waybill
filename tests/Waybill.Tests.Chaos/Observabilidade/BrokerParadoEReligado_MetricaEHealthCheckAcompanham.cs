using System.Diagnostics.Metrics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Waybill.EntityFrameworkCore.Dispatching;
using Waybill.RabbitMQ;
using Waybill.Tests.Chaos.Broker;
using Waybill.Tests.Integration;
using Waybill.Tests.Integration.G2;
using Waybill.Tests.Integration.Observabilidade;

namespace Waybill.Tests.Chaos.Observabilidade;

// What the operator sees during a real broker outage, wired only through the public API: the health check goes to
// Degraded and back to Healthy, the oldest pending age grows and returns to 0, and nothing goes to the DLQ.
[Collection(ChaosBrokerCollection.Name)]
public sealed class BrokerParadoEReligado_MetricaEHealthCheckAcompanham(PostgresFixture postgres, ChaosBrokerFixture broker)
{
    [Fact]
    public async Task BrokerParadoEReligado_HealthCheckDegradadoEVolta_IdadeCresceEVoltaAZero()
    {
        var ct = TestContext.Current.CancellationToken;
        var database = await TestDatabase.CreateAsync(postgres);
        var (exchange, queue) = await broker.DeclareTopologyAsync();
        var ids = await DispatcherHarness.EnqueueAsync(database, 100);
        await using var services = new ServiceCollection()
            .AddLogging(logging => logging.SetMinimumLevel(LogLevel.Warning))
            .AddMetrics()
            .AddWaybill(o => o.MaxPayloadBytes = 64 * 1024)
            .AddWaybillRabbitMQ(o =>
            {
                o.Uri = broker.ProxiedUri;
                o.Exchange = exchange;
            })
            .AddWaybillDispatcher(o =>
            {
                o.ConnectionString = database.ConnectionString;
                o.PollingInterval = TimeSpan.FromMilliseconds(100);
                o.PublishTimeout = TimeSpan.FromSeconds(3);
                o.LeaseMargin = TimeSpan.FromSeconds(2);
                o.MetricsInterval = TimeSpan.FromMilliseconds(200);
            })
            .AddHealthChecks().AddWaybillDispatcherCheck().Services
            .BuildServiceProvider();
        var factory = services.GetRequiredService<IMeterFactory>();
        var health = services.GetRequiredService<HealthCheckService>();
        var hosted = services.GetServices<IHostedService>().ToList();

        await broker.SetBrokerReachableAsync(false);
        try
        {
            foreach (var service in hosted)
                await service.StartAsync(ct);

            var degraded = await WaitForAsync(health, HealthStatus.Degraded, ct);
            var ageEarly = GaugeReader.Read(factory).Value;
            await Task.Delay(TimeSpan.FromSeconds(2), ct);
            var ageLater = GaugeReader.Read(factory).Value;

            Assert.Contains("broker", degraded.Description);
            Assert.True(ageEarly > 0, $"age early {ageEarly}");
            Assert.True(ageLater >= ageEarly + 1.5, $"age did not grow: {ageEarly} -> {ageLater}");
            Assert.Equal(0, await database.CountAsync("dlq"));
        }
        finally
        {
            await broker.SetBrokerReachableAsync(true);
        }

        await WaitForAsync(health, HealthStatus.Healthy, ct);
        await WaitUntilAsync(() => Task.FromResult(GaugeReader.Read(factory).Value == 0), ct);
        foreach (var service in hosted)
            await service.StopAsync(ct);

        Assert.Equal(100, await database.CountAsync("published"));
        Assert.Equal(0, await database.CountAsync("dlq"));
        Assert.Equal(ids.Select(id => id.ToString()).Order(), (await broker.DrainMessageIdsAsync(queue)).Distinct().Order());
    }

    private static async Task<HealthReportEntry> WaitForAsync(HealthCheckService health, HealthStatus expected, CancellationToken ct)
    {
        HealthReportEntry entry = default;
        await WaitUntilAsync(async () => (entry = (await health.CheckHealthAsync(ct)).Entries["waybill-dispatcher"]).Status == expected, ct);
        return entry;
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> condition, CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromMinutes(2));
        while (!await condition())
            await Task.Delay(100, deadline.Token);
    }
}
