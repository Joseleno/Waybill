using Npgsql;
using Waybill.Tests.Integration.G2;

namespace Waybill.Tests.Integration.G4;

// Under a backlog the dispatcher runs full batches back to back. Partition upkeep (heartbeat, renewal, fair share)
// is several round trips, so it runs at most every (PartitionLease − lease) / 4, which still renews a partition long
// before the claim's "held for longer than a row lease" check would fail (ADR 0007). Counted with a trigger on the
// heartbeat.
[Collection(PostgresCollection.Name)]
public sealed class G4_LotesCheiosEmSequencia_ManutencaoEspacada(PostgresFixture postgres)
{
    [Fact]
    public async Task G4_LotesCheiosEmSequencia_ManutencaoEspacada_UmaPorIntervalo()
    {
        var ct = TestContext.Current.CancellationToken;
        var database = await TestDatabase.CreateAsync(postgres);
        await database.ScalarAsync("""
            CREATE TABLE heartbeat_audit (at timestamptz);
            CREATE FUNCTION audit_heartbeats() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN INSERT INTO heartbeat_audit VALUES (clock_timestamp()); RETURN NEW; END $$;
            CREATE TRIGGER audit_heartbeats AFTER INSERT OR UPDATE ON waybill.outbox_instances FOR EACH ROW EXECUTE FUNCTION audit_heartbeats();
            SELECT 1;
            """);
        await DispatcherHarness.EnqueueAsync(database, 500);
        await using var dataSource = NpgsqlDataSource.Create(database.ConnectionString);
        var options = OrderingHarness.Options(database, partitions: 4); // lease 30 s, partition lease 60 s: upkeep every 7.5 s
        options.BatchSize = 50;
        var dispatcher = OrderingHarness.Create(dataSource, new FakeTransport(), options);
        Assert.Null(await dispatcher.CheckOrderingAsync(atStartup: true, ct));

        for (var i = 0; i < 10; i++)
            Assert.Equal(50, (await dispatcher.RunOnceAsync(ct)).Claimed);

        Assert.Equal(500, await database.CountAsync("published"));
        Assert.Equal(1, await database.ScalarAsync("SELECT count(*) FROM heartbeat_audit"));
    }
}
