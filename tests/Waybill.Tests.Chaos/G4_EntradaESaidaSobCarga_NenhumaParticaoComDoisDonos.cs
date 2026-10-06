using System.Diagnostics;
using Npgsql;
using Waybill.EntityFrameworkCore.Dispatching;
using Waybill.Tests.Integration;
using Waybill.Tests.Integration.G2;
using Waybill.Tests.Integration.G4;

namespace Waybill.Tests.Chaos;

// Eight slots of ordering dispatchers under load. Each slot runs an instance for a while, then shuts it down
// gracefully or lets it die (no more cycles, nothing handed back), and starts a new one. The transport sometimes stalls
// longer than the partition lease, so an instance can lose its partitions in the middle of a batch. Two oracles, both
// triggers, no hook in production code (ADR 0007):
// - tenures of a partition never overlap: a new owner starts only after the previous one released or its lease ran out;
// - every claim of a keyed row happens inside a tenure of the claiming instance over that row's partition.
[Collection(PostgresCollection.Name)]
public sealed class G4_EntradaESaidaSobCarga_NenhumaParticaoComDoisDonos(PostgresFixture postgres)
{
    private const int P = 16;

    [Fact]
    public Task G4_EntradaESaidaSobCarga_NenhumaParticaoComDoisDonos_Curto() => Run(TimeSpan.FromSeconds(20));

    [Fact]
    [Trait("Category", "Long")]
    public Task G4_EntradaESaidaSobCarga_NenhumaParticaoComDoisDonos_DezMinutos() => Run(TimeSpan.FromMinutes(10));

    private async Task Run(TimeSpan duration)
    {
        var ct = TestContext.Current.CancellationToken;
        var database = await TestDatabase.CreateAsync(postgres);
        await database.ScalarAsync(AuditSql);
        await DispatcherHarness.EnqueueAsync(database, 2_000);

        var stalls = 0;
        var transport = new FakeTransport(async (_, batch, _) =>
        {
            if (Random.Shared.NextDouble() < 0.05)
            {
                Interlocked.Increment(ref stalls);
                await Task.Delay(TimeSpan.FromMilliseconds(900), CancellationToken.None); // past the partition lease
            }
            return batch.Select(_ => PublishResult.Confirmed).ToList();
        });
        var options = OrderingHarness.Options(database, P, partitionLease: TimeSpan.FromMilliseconds(700));
        options.BatchSize = 50;
        options.PublishTimeout = TimeSpan.FromMilliseconds(200);
        options.LeaseMargin = TimeSpan.FromMilliseconds(150); // row lease 350 ms, partition lease twice that

        await using var dataSource = NpgsqlDataSource.Create(database.ConnectionString);
        Assert.Null(await OrderingHarness.Create(dataSource, transport, options).CheckOrderingAsync(atStartup: true, ct));

        var (graceful, deaths) = (0, 0);
        var clock = Stopwatch.StartNew();
        var slots = Enumerable.Range(0, 8).Select(_ => Task.Run(async () =>
        {
            while (clock.Elapsed < duration)
            {
                var dispatcher = OrderingHarness.Create(dataSource, transport, options);
                var lifetime = Stopwatch.StartNew();
                var life = TimeSpan.FromMilliseconds(Random.Shared.Next(500, 3_000));
                while (lifetime.Elapsed < life && clock.Elapsed < duration)
                {
                    if ((await dispatcher.RunOnceAsync(CancellationToken.None)).Claimed == 0)
                        await Task.Delay(20, CancellationToken.None);
                }
                if (Random.Shared.NextDouble() < 0.5)
                {
                    await dispatcher.ReleaseOwnedAsync(CancellationToken.None);
                    Interlocked.Increment(ref graceful);
                }
                else
                {
                    Interlocked.Increment(ref deaths); // just stops: its rows and partitions wait for their leases
                }
                await Task.Delay(Random.Shared.Next(0, 300), CancellationToken.None);
            }
        }, CancellationToken.None)).ToList();

        var produced = 2_000;
        while (clock.Elapsed < duration)
        {
            produced += (await DispatcherHarness.EnqueueAsync(database, 25)).Count;
            await Task.Delay(200, ct);
        }
        await Task.WhenAll(slots);

        // Drain what is left with one survivor, once every dead instance's leases ran out.
        var survivor = OrderingHarness.Create(dataSource, transport, options);
        var drain = Stopwatch.StartNew();
        while (await database.CountAsync("published") < produced && drain.Elapsed < TimeSpan.FromMinutes(2))
        {
            if ((await survivor.RunOnceAsync(ct)).Claimed == 0)
                await Task.Delay(100, ct);
        }

        var overlaps = await database.ScalarAsync("""
            SELECT count(*) FROM (
                SELECT acquired_at,
                       lag(LEAST(lease_until, coalesce(ended_at, lease_until))) OVER (PARTITION BY partition ORDER BY epoch) AS previous_end
                FROM tenure_audit) t
            WHERE acquired_at < previous_end - interval '5 milliseconds'
            """);
        var outside = await database.ScalarAsync($"""
            SELECT count(*) FROM claim_audit c
            WHERE NOT EXISTS (
                SELECT 1 FROM tenure_audit t
                WHERE t.partition = c.key_hash % {P} AND t.owner = c.owner
                  AND c.claimed_at >= t.acquired_at - interval '5 milliseconds'
                  AND c.claimed_at <= LEAST(t.lease_until, coalesce(t.ended_at, t.lease_until)) + interval '5 milliseconds')
            """);
        var tenures = await database.ScalarAsync("SELECT count(*) FROM tenure_audit");
        var owners = await database.ScalarAsync("SELECT count(DISTINCT owner) FROM tenure_audit");
        var received = transport.Received.Select(m => m.MessageId).Distinct().Count();

        TestContext.Current.SendDiagnosticMessage(
            $"produced {produced}, received {received}, tenures {tenures}, owners {owners}, graceful {graceful}, deaths {deaths}, stalls {stalls}, overlaps {overlaps}, claims outside a tenure {outside}");

        Assert.Equal(0, overlaps);   // never two valid owners of one partition
        Assert.Equal(0, outside);    // never a keyed claim outside the claimer's tenure
        Assert.Equal(produced, await database.CountAsync("published"));
        Assert.Equal(produced, received);
        Assert.True(graceful > 0 && deaths > 0 && stalls > 0, $"graceful {graceful}, deaths {deaths}, stalls {stalls}");
        Assert.True(tenures > P, "no partition ever changed hands");
    }

    // The wall clock of Docker/WSL2 steps back up to ~1.7 ms (stage 0 spike), hence the 5 ms tolerances.
    private const string AuditSql = """
        CREATE TABLE tenure_audit (partition int, epoch bigint, owner text, acquired_at timestamptz, lease_until timestamptz, ended_at timestamptz);
        CREATE FUNCTION audit_tenures() RETURNS trigger LANGUAGE plpgsql AS $$
        BEGIN
            -- A new tenure does not end the previous one: only the owner's own release does. Otherwise the previous
            -- tenure stays valid until its lease_until, and a takeover before that is an overlap.
            IF NEW.epoch <> OLD.epoch THEN
                INSERT INTO tenure_audit VALUES (NEW.partition, NEW.epoch, NEW.owner, clock_timestamp(), NEW.lease_until, NULL);
            ELSIF NEW.owner IS NULL AND OLD.owner IS NOT NULL THEN
                UPDATE tenure_audit SET ended_at = clock_timestamp() WHERE partition = OLD.partition AND epoch = OLD.epoch AND ended_at IS NULL;
            ELSIF NEW.lease_until IS DISTINCT FROM OLD.lease_until THEN
                UPDATE tenure_audit SET lease_until = NEW.lease_until WHERE partition = NEW.partition AND epoch = NEW.epoch;
            END IF;
            RETURN NEW;
        END $$;
        CREATE TRIGGER audit_tenures AFTER UPDATE ON waybill.outbox_partitions FOR EACH ROW EXECUTE FUNCTION audit_tenures();

        CREATE TABLE claim_audit (id uuid, key_hash int, owner text, claimed_at timestamptz);
        CREATE FUNCTION audit_claims() RETURNS trigger LANGUAGE plpgsql AS $$
        BEGIN
            IF NEW.status = 'claimed' AND NEW.fence <> OLD.fence AND NEW.key IS NOT NULL THEN
                INSERT INTO claim_audit VALUES (NEW.id, NEW.key_hash, NEW.owner, clock_timestamp());
            END IF;
            RETURN NEW;
        END $$;
        CREATE TRIGGER audit_claims AFTER UPDATE ON waybill.outbox FOR EACH ROW EXECUTE FUNCTION audit_claims();
        SELECT 1;
        """;
}
