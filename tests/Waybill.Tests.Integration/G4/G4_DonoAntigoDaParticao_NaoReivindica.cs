using Npgsql;
using Waybill.EntityFrameworkCore.Dispatching;
using Waybill.Tests.Integration.G2;

namespace Waybill.Tests.Integration.G4;

// An instance that paused right after renewing (a long GC, a frozen VM) wakes up believing it still holds its
// partition, after another instance took it. The claim reads ownership in its own snapshot, so the old owner claims
// nothing of that partition; nor does it claim while the partition is held for less than the row lease it would take
// (ADR 0007). Two owners claiming the same key at once is the inversion the stage 0 spike found with M >= 2.
[Collection(PostgresCollection.Name)]
public sealed class G4_DonoAntigoDaParticao_NaoReivindica(PostgresFixture postgres)
{
    private static readonly TimeSpan RowLease = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task G4_DonoAntigoDaParticao_NaoReivindica_NemComLeaseDaParticaoCurto()
    {
        var ct = TestContext.Current.CancellationToken;
        var database = await TestDatabase.CreateAsync(postgres);
        await using var dataSource = NpgsqlDataSource.Create(database.ConnectionString);
        var store = new OutboxStore(dataSource);
        var partitions = new PartitionStore(dataSource);
        var settings = new OrderingSettings(1, TimeSpan.FromSeconds(60));
        await partitions.EnsureSettingsAsync(settings, ct);

        Assert.Single(await partitions.MaintainAsync("old-owner", settings, ct)); // renewed, then it pauses
        await DispatcherHarness.EnqueueAsync(database, 10);

        // Held, but for less than the row lease it would take: no claim.
        await database.ScalarAsync("UPDATE waybill.outbox_partitions SET lease_until = clock_timestamp() + interval '10 seconds'");
        Assert.Empty(await store.ClaimAsync("old-owner", 100, RowLease, ct, partitions: 1));

        // Lost while paused: the lease ran out and another instance took the partition.
        await database.ScalarAsync("UPDATE waybill.outbox_partitions SET lease_until = clock_timestamp() - interval '1 second'");
        Assert.Equal([0], (await partitions.MaintainAsync("new-owner", settings, ct)).Keys);
        Assert.Empty(await store.ClaimAsync("old-owner", 100, RowLease, ct, partitions: 1)); // wakes up with its old belief

        Assert.Equal(10, (await store.ClaimAsync("new-owner", 100, RowLease, ct, partitions: 1)).Count);
        Assert.Equal(2, await database.ScalarAsync("SELECT epoch FROM waybill.outbox_partitions"));
    }
}
