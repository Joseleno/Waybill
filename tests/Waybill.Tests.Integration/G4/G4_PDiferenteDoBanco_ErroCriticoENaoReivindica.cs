using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using Npgsql;
using Waybill.EntityFrameworkCore.Dispatching;
using Waybill.Tests.Integration.G2;
using Waybill.Tests.Integration.Observabilidade;

namespace Waybill.Tests.Integration.G4;

// P and the partition lease are global: two dispatchers that disagree on P would map the same key to different
// partitions and both claim it. A dispatcher whose values differ from the stored ones stops with a critical error
// that says what to change, claims nothing, and its health check reports the loop as not running (ADR 0007).
[Collection(PostgresCollection.Name)]
public sealed class G4_PDiferenteDoBanco_ErroCriticoENaoReivindica(PostgresFixture postgres)
{
    [Theory]
    [InlineData(8, 60, "Partitions = 4")]
    [InlineData(4, 120, "PartitionLease = 00:01:00")]
    public async Task G4_PDiferenteDoBanco_ErroCriticoENaoReivindica_DizOQueMudar(int partitions, int partitionLeaseSeconds, string stored)
    {
        var ct = TestContext.Current.CancellationToken;
        var database = await TestDatabase.CreateAsync(postgres);
        await using (var dataSource = NpgsqlDataSource.Create(database.ConnectionString))
            await new PartitionStore(dataSource).EnsureSettingsAsync(new OrderingSettings(4, TimeSpan.FromSeconds(60)), ct);
        await DispatcherHarness.EnqueueAsync(database, 10);
        var logs = new LogSink();
        await using var harness = new HealthHarness(database, new FakeTransport(), logs,
            dispatcher: o =>
            {
                o.OrderByKey = true;
                o.Partitions = partitions;
                o.PartitionLease = TimeSpan.FromSeconds(partitionLeaseSeconds);
            });

        await harness.Dispatcher.StartAsync(ct);
        var entry = await harness.WaitForAsync(HealthStatus.Unhealthy, ct);

        Assert.Contains("not running", entry.Description);
        Assert.Contains(logs.Entries, e => e.Level == LogLevel.Critical && e.Message.Contains(stored) && e.Message.Contains("OPERATIONS.md"));
        Assert.Equal(10, await database.CountAsync("pending"));
        Assert.Equal(0, await database.ScalarAsync("SELECT count(*) FROM waybill.outbox_partitions WHERE owner IS NOT NULL"));
        await harness.Dispatcher.StopAsync(ct);
    }
}
