using System.Diagnostics;
using Npgsql;
using Waybill.Tests.Integration;
using Waybill.Tests.Integration.G2;

namespace Waybill.Tests.Chaos;

// Eight dispatchers on one outbox with a short lease, a transport that sometimes stalls past the lease (forcing
// reclaims and late, fenced markings) and sometimes fails. The oracle is a trigger on waybill.outbox that records
// every claim and when it ended: for each row, a claim may only start after the previous one ended or its lease
// ran out. No hook in production code.
[Collection(PostgresCollection.Name)]
public sealed class G2_OitoDispatchers_NenhumaLinhaComDoisLeasesValidos(PostgresFixture postgres)
{
    [Fact]
    public Task G2_OitoDispatchers_NenhumaLinhaComDoisLeasesValidos_Curto() =>
        Run(messages: 3_000, produceFor: TimeSpan.Zero);

    [Fact]
    [Trait("Category", "Long")]
    public Task G2_OitoDispatchers_NenhumaLinhaComDoisLeasesValidos_DezMinutos() =>
        Run(messages: 3_000, produceFor: TimeSpan.FromMinutes(10));

    private async Task Run(int messages, TimeSpan produceFor)
    {
        var ct = TestContext.Current.CancellationToken;
        var database = await TestDatabase.CreateAsync(postgres);
        await database.ScalarAsync(AuditTriggerSql);
        await DispatcherHarness.EnqueueAsync(database, messages);

        var stalls = 0;
        var transport = new FakeTransport(async (_, batch, _) =>
        {
            var roll = Random.Shared.NextDouble();
            if (roll < 0.10)
            {
                Interlocked.Increment(ref stalls);
                await Task.Delay(TimeSpan.FromMilliseconds(600), CancellationToken.None); // stuck past the lease, ignoring the timeout
            }
            else if (roll < 0.15)
            {
                throw new IOException("connection reset by broker");
            }
            return batch.Select(_ => PublishResult.Confirmed).ToList();
        });
        var options = DispatcherHarness.Options(database, o =>
        {
            o.BatchSize = 50;
            o.PublishTimeout = TimeSpan.FromMilliseconds(200);
            o.LeaseMargin = TimeSpan.FromMilliseconds(150);   // lease = 350 ms
        });

        await using var dataSource = NpgsqlDataSource.Create(database.ConnectionString);
        using var stop = new CancellationTokenSource();
        var dispatchers = Enumerable.Range(0, 8).Select(_ => DispatcherHarness.Create(dataSource, transport, options)).ToList();
        var loops = dispatchers.Select(d => Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                if (await d.RunOnceAsync(CancellationToken.None) == 0)
                    await Task.Delay(20, CancellationToken.None);
            }
        }, CancellationToken.None)).ToList();

        var produced = messages;
        var clock = Stopwatch.StartNew();
        while (clock.Elapsed < produceFor)
        {
            produced += (await DispatcherHarness.EnqueueAsync(database, 50)).Count;
            await Task.Delay(200, ct);
        }
        while (clock.Elapsed < produceFor + TimeSpan.FromMinutes(2) && await database.CountAsync("published") < produced)
            await Task.Delay(200, ct);
        await stop.CancelAsync();
        await Task.WhenAll(loops);

        var overlaps = await database.ScalarAsync("""
            SELECT count(*) FROM (
                SELECT claimed_at,
                       lag(LEAST(lease_until, coalesce(ended_at, lease_until))) OVER (PARTITION BY id ORDER BY fence) AS previous_end
                FROM claim_audit) t
            WHERE claimed_at < previous_end - interval '5 milliseconds'
            """);
        var owners = await database.ScalarAsync("SELECT count(DISTINCT owner) FROM claim_audit");
        var reclaims = await database.ScalarAsync("SELECT count(*) FROM claim_audit WHERE fence > 1");
        var received = transport.Received.Select(m => m.MessageId).Distinct().Count();

        TestContext.Current.SendDiagnosticMessage(
            $"produced {produced}, received {received}, stalls {stalls}, reclaims {reclaims}, owners {owners}, overlaps {overlaps}");

        Assert.Equal(produced, await database.CountAsync("published"));
        Assert.Equal(produced, received);              // at least once, each
        Assert.Equal(8, owners);                       // every instance did work
        Assert.True(stalls > 0 && reclaims > 0, "the scenario produced no stalled publish or reclaim");
        Assert.Equal(0, overlaps);                     // never two valid leases on one row
        Assert.Equal(0, await database.ScalarAsync("SELECT coalesce(max(attempts), 0) FROM waybill.outbox"));
    }

    // The wall clock of Docker/WSL2 steps back up to ~1.7 ms (stage 0 spike), hence the 5 ms tolerance; a real
    // double claim overlaps by up to the whole lease (350 ms).
    private const string AuditTriggerSql = """
        CREATE TABLE claim_audit (id uuid, fence bigint, owner text, claimed_at timestamptz, lease_until timestamptz, ended_at timestamptz);
        CREATE FUNCTION audit_claims() RETURNS trigger LANGUAGE plpgsql AS $$
        BEGIN
            IF OLD.status = 'claimed' AND (NEW.status <> 'claimed' OR NEW.fence <> OLD.fence) THEN
                UPDATE claim_audit SET ended_at = clock_timestamp() WHERE id = OLD.id AND fence = OLD.fence AND ended_at IS NULL;
            END IF;
            IF NEW.status = 'claimed' AND NEW.fence <> OLD.fence THEN
                INSERT INTO claim_audit (id, fence, owner, claimed_at, lease_until) VALUES (NEW.id, NEW.fence, NEW.owner, clock_timestamp(), NEW.lease_until);
            END IF;
            RETURN NEW;
        END $$;
        CREATE TRIGGER audit_claims AFTER UPDATE ON waybill.outbox FOR EACH ROW EXECUTE FUNCTION audit_claims();
        SELECT 1;
        """;
}
