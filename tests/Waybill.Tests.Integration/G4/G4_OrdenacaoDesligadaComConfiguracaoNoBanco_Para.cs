using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using Npgsql;
using Waybill.EntityFrameworkCore.Dispatching;
using Waybill.Tests.Integration.G2;
using Waybill.Tests.Integration.Observabilidade;

namespace Waybill.Tests.Integration.G4;

// A dispatcher that does not order, next to ones that do, would claim keyed rows outside any partition and break the
// order the others keep. It stops with a critical error when it finds the ordering settings: at startup, and also when
// they appear while it runs, checked every partition lease (ADR 0007).
[Collection(PostgresCollection.Name)]
public sealed class G4_OrdenacaoDesligadaComConfiguracaoNoBanco_Para(PostgresFixture postgres)
{
    private static readonly OrderingSettings Settings = new(4, TimeSpan.FromSeconds(60));

    [Fact]
    public async Task G4_OrdenacaoDesligadaComConfiguracaoNoBanco_Para_NoStartup()
    {
        var ct = TestContext.Current.CancellationToken;
        var database = await TestDatabase.CreateAsync(postgres);
        await EnsureSettingsAsync(database, ct);
        await DispatcherHarness.EnqueueAsync(database, 10);
        var logs = new LogSink();
        await using var harness = new HealthHarness(database, new FakeTransport(), logs);

        await harness.Dispatcher.StartAsync(ct);
        await harness.WaitForAsync(HealthStatus.Unhealthy, ct);

        Assert.Contains(logs.Entries, e => e.Level == LogLevel.Critical && e.Message.Contains("OrderByKey"));
        Assert.Equal(10, await database.CountAsync("pending"));
        await harness.Dispatcher.StopAsync(ct);
    }

    [Fact]
    public async Task G4_OrdenacaoDesligadaComConfiguracaoNoBanco_Para_QuandoApareceComElaRodando()
    {
        var ct = TestContext.Current.CancellationToken;
        var database = await TestDatabase.CreateAsync(postgres);
        var logs = new LogSink();
        await using var harness = new HealthHarness(database, new FakeTransport(), logs,
            dispatcher: o => o.PartitionLease = TimeSpan.FromMilliseconds(300)); // how often it checks again
        await harness.Dispatcher.StartAsync(ct);
        await harness.WaitForAsync(HealthStatus.Healthy, ct);

        await EnsureSettingsAsync(database, ct); // another dispatcher starts ordering by key
        await harness.WaitForAsync(HealthStatus.Unhealthy, ct);

        Assert.Contains(logs.Entries, e => e.Level == LogLevel.Critical && e.Message.Contains("OrderByKey"));
        await DispatcherHarness.EnqueueAsync(database, 5);
        await Task.Delay(500, ct);
        Assert.Equal(5, await database.CountAsync("pending")); // stopped: claims nothing
        await harness.Dispatcher.StopAsync(ct);
    }

    private static async Task EnsureSettingsAsync(TestDatabase database, CancellationToken ct)
    {
        await using var dataSource = NpgsqlDataSource.Create(database.ConnectionString);
        await new PartitionStore(dataSource).EnsureSettingsAsync(Settings, ct);
    }
}
