using Npgsql;
using Waybill.Tests.Integration.G2;

namespace Waybill.Tests.Integration.G4;

// The first dispatcher that orders by key writes P and the partition lease for everyone, creates the P partitions,
// and, alone, holds all of them (ADR 0007).
[Collection(PostgresCollection.Name)]
public sealed class G4_PrimeiraInstancia_CriaConfiguracaoEParticoes(PostgresFixture postgres)
{
    [Fact]
    public async Task G4_PrimeiraInstancia_CriaConfiguracaoEParticoes_EFicaComTodas()
    {
        var ct = TestContext.Current.CancellationToken;
        var database = await TestDatabase.CreateAsync(postgres);
        await DispatcherHarness.EnqueueAsync(database, 20);
        await using var dataSource = NpgsqlDataSource.Create(database.ConnectionString);
        var dispatcher = OrderingHarness.Create(dataSource, new FakeTransport(), OrderingHarness.Options(database, partitions: 4));

        Assert.Null(await dispatcher.CheckOrderingAsync(atStartup: true, ct));
        Assert.Equal(1, await database.ScalarAsync("SELECT count(*) FROM waybill.settings WHERE partitions = 4 AND partition_lease = interval '60 seconds'"));
        Assert.Equal(4, await database.ScalarAsync("SELECT count(*) FROM waybill.outbox_partitions WHERE owner IS NULL AND epoch = 0"));

        await dispatcher.RunOnceAsync(ct);

        Assert.Equal([0, 1, 2, 3], dispatcher.HeldPartitions.Keys);
        Assert.All(dispatcher.HeldPartitions.Values, epoch => Assert.Equal(1, epoch));
        Assert.Equal(4, await database.ScalarAsync($"SELECT count(*) FROM waybill.outbox_partitions WHERE owner = '{dispatcher.Owner}' AND lease_until > clock_timestamp()"));
        Assert.Equal(1, await database.ScalarAsync($"SELECT count(*) FROM waybill.outbox_instances WHERE owner = '{dispatcher.Owner}'"));
        Assert.Equal(20, await database.CountAsync("published"));
    }
}
