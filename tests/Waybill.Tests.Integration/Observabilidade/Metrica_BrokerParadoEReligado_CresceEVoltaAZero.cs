using System.Diagnostics.Metrics;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Waybill.EntityFrameworkCore.Dispatching;
using Waybill.Tests.Integration.G2;

namespace Waybill.Tests.Integration.Observabilidade;

// The operator's signal for a stuck outbox: while the broker is down the age of the oldest message still to be
// published grows with every sample, measured on the database clock; once the backlog drains it drops back to 0.
[Collection(PostgresCollection.Name)]
public sealed class Metrica_BrokerParadoEReligado_CresceEVoltaAZero(PostgresFixture postgres)
{
    [Fact]
    public async Task Metrica_BrokerParado_IdadeCresce_Religado_VoltaAZero()
    {
        var ct = TestContext.Current.CancellationToken;
        var database = await TestDatabase.CreateAsync(postgres);
        await DispatcherHarness.EnqueueAsync(database, 10);
        await using var services = new ServiceCollection().AddMetrics().BuildServiceProvider();
        var factory = services.GetRequiredService<IMeterFactory>();
        await using var dataSource = NpgsqlDataSource.Create(database.ConnectionString);
        using var metrics = new OutboxMetrics(factory);
        var sampler = new OldestPendingSampler(new OutboxStore(dataSource), metrics);
        var brokerDown = DispatcherHarness.Create(dataSource, new FakeTransport((_, batch, _) =>
            Task.FromResult<IReadOnlyList<PublishResult>>(batch.Select(_ => PublishResult.RetryAfter(TransportFailure.Connection)).ToList())),
            DispatcherHarness.Options(database));

        await brokerDown.RunOnceAsync(ct);
        await sampler.SampleAsync(ct);
        var first = GaugeReader.Read(factory).Value;
        await Task.Delay(TimeSpan.FromSeconds(1.5), ct);
        await brokerDown.RunOnceAsync(ct);
        await sampler.SampleAsync(ct);
        var second = GaugeReader.Read(factory).Value;

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.True(first > 0, $"first sample {first}");
        Assert.InRange(second!.Value - first!.Value, 1.0, 10.0); // grew by about the time that passed

        var brokerBack = DispatcherHarness.Create(dataSource, new FakeTransport(), DispatcherHarness.Options(database));
        while (await database.ScalarAsync("SELECT count(*) FROM waybill.outbox WHERE status IN ('pending', 'claimed')") > 0)
            await brokerBack.RunOnceAsync(ct);
        await sampler.SampleAsync(ct);

        Assert.Equal(0d, GaugeReader.Read(factory).Value);
    }
}
