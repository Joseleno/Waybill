using Microsoft.Extensions.Diagnostics.HealthChecks;
using Waybill.Tests.Integration.G2;
using Waybill.Tests.Integration.Observabilidade;

namespace Waybill.Tests.Integration.G4;

// A dispatcher waits between cycles: the polling interval when idle, the breaker or the database backoff when the
// broker or the database fail. With ordering on, none of those waits may outlast the partition lease, or the
// partitions expire between cycles and change tenure (epoch) over and over while nothing is wrong (ADR 0007).
[Collection(PostgresCollection.Name)]
public sealed class G4_EsperaEntreCiclosMaiorQueOLease_NaoPerdeParticoes(PostgresFixture postgres)
{
    [Fact]
    public async Task G4_EsperaEntreCiclosMaiorQueOLease_NaoPerdeParticoes_MesmoMandato()
    {
        var ct = TestContext.Current.CancellationToken;
        var database = await TestDatabase.CreateAsync(postgres);
        await using var harness = new HealthHarness(database, new FakeTransport(),
            waybill: o => o.OrderByKey = true,
            dispatcher: o =>
            {
                o.Partitions = 4;
                o.PollingInterval = TimeSpan.FromSeconds(5); // idle cycles much longer than the partition lease
                o.PublishTimeout = TimeSpan.FromMilliseconds(200);
                o.LeaseMargin = TimeSpan.FromMilliseconds(150);
                o.PartitionLease = TimeSpan.FromMilliseconds(700);
            });

        await harness.Dispatcher.StartAsync(ct);
        await harness.WaitForAsync(HealthStatus.Healthy, ct);
        await Task.Delay(TimeSpan.FromSeconds(3), ct);

        Assert.Equal(4, await database.ScalarAsync("SELECT count(*) FROM waybill.outbox_partitions WHERE owner IS NOT NULL AND lease_until > clock_timestamp()"));
        Assert.Equal(1, await database.ScalarAsync("SELECT max(epoch) FROM waybill.outbox_partitions"));
        await harness.Dispatcher.StopAsync(ct);
    }
}
