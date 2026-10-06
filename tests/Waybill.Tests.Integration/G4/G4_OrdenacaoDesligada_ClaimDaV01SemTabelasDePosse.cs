using Npgsql;
using Waybill.Tests.Integration.G2;

namespace Waybill.Tests.Integration.G4;

// Ordering is off by default, and then nothing of it runs: no settings, no partitions, no heartbeat, and the claim of
// v0.1. Who does not ask for ordering does not pay for it (ADR 0007).
[Collection(PostgresCollection.Name)]
public sealed class G4_OrdenacaoDesligada_ClaimDaV01SemTabelasDePosse(PostgresFixture postgres)
{
    [Fact]
    public async Task G4_OrdenacaoDesligada_ClaimDaV01SemTabelasDePosse_PublicaTudo()
    {
        var ct = TestContext.Current.CancellationToken;
        var database = await TestDatabase.CreateAsync(postgres);
        await DispatcherHarness.EnqueueAsync(database, 20);
        await using var dataSource = NpgsqlDataSource.Create(database.ConnectionString);
        var dispatcher = DispatcherHarness.Create(dataSource, new FakeTransport(), DispatcherHarness.Options(database));

        Assert.Null(await dispatcher.CheckOrderingAsync(atStartup: true, ct));
        await dispatcher.RunOnceAsync(ct);
        await dispatcher.ReleaseOwnedAsync(ct);

        Assert.Equal(20, await database.CountAsync("published"));
        Assert.Empty(dispatcher.HeldPartitions);
        Assert.Equal(0, await database.ScalarAsync(
            "SELECT (SELECT count(*) FROM waybill.settings) + (SELECT count(*) FROM waybill.outbox_partitions) + (SELECT count(*) FROM waybill.outbox_instances)"));
    }
}
