using Npgsql;
using Waybill.Tests.Integration.G2;

namespace Waybill.Tests.Integration.Convivencia;

// OPERATIONS.md, "Upgrading from 0.1": the schema is migrated first, and the applications move to 0.2 one by one. Until
// the last one does, producers and dispatchers of both versions share the 0.2 schema, with ordering off. Nothing is
// lost: every message either version writes is published by one dispatcher or the other.
[Collection(PostgresCollection.Name)]
public sealed class Convivencia_JanelaDoUpgrade(PostgresFixture postgres)
{
    [Fact]
    public async Task Convivencia_JanelaDoUpgrade_01E02Juntas_NenhumaSePerde()
    {
        var ct = TestContext.Current.CancellationToken;
        var database = await TestDatabase.CreateAsync(postgres);
        await using var dataSource = NpgsqlDataSource.Create(database.ConnectionString);
        var transport = new FakeTransport();
        var dispatcher = DispatcherHarness.Create(dataSource, transport, DispatcherHarness.Options(database));
        Assert.Null(await dispatcher.CheckOrderingAsync(atStartup: true, ct));

        await using var legacyDispatcher = await LegacyHost.StartAsync("dispatch", database.ConnectionString);
        await legacyDispatcher.WaitForAsync("STARTED", TimeSpan.FromSeconds(60));

        // Half of each version's messages carry a key, as an application that enqueues per aggregate would.
        await using var legacyUnkeyed = await LegacyHost.StartAsync("produce", database.ConnectionString, "100", "-");
        await using var legacyKeyed = await LegacyHost.StartAsync("produce", database.ConnectionString, "100", "mod:5");
        var produced = Task.Run(async () =>
        {
            var ids = new List<Guid>();
            for (var i = 0; i < 20; i++)
                ids.AddRange(await DispatcherHarness.EnqueueAsync(database, 10, key: n => n % 2 == 0 ? null : $"current-{n % 5}"));
            return ids;
        }, ct);

        var current = new List<Guid>();
        using (var stop = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            stop.CancelAfter(TimeSpan.FromMinutes(2));
            while (!produced.IsCompleted || !legacyUnkeyed.HasExited || !legacyKeyed.HasExited
                   || await database.ScalarAsync("SELECT count(*) FROM waybill.outbox WHERE status IN ('pending', 'claimed')") > 0)
            {
                stop.Token.ThrowIfCancellationRequested();
                if ((await dispatcher.RunOnceAsync(ct)).Claimed == 0)
                    await Task.Delay(50, ct);
            }
            current.AddRange(await produced);
        }
        await legacyUnkeyed.ExitedAsync();
        await legacyKeyed.ExitedAsync();
        await legacyDispatcher.StopAsync();

        var legacy = legacyUnkeyed.Lines("ENQUEUED ").Concat(legacyKeyed.Lines("ENQUEUED ")).Select(Guid.Parse).ToList();
        var published = legacyDispatcher.Lines("PUBLISHED ").Select(Guid.Parse)
            .Concat(transport.Received.Select(m => m.MessageId)).ToHashSet();
        Assert.Equal(200, legacy.Count);
        Assert.Equal(200, current.Count);
        var lost = legacy.Concat(current).Where(id => !published.Contains(id)).ToList();
        Assert.Empty(lost);
        Assert.Equal(400, await database.CountAsync("published"));

        // Both dispatchers took part, and the 0.1 rows went in unnumbered, as with ordering off they must.
        Assert.NotEmpty(legacyDispatcher.Lines("PUBLISHED "));
        Assert.NotEmpty(transport.Received);
        Assert.Equal(0, await database.ScalarAsync("SELECT count(*) FROM waybill.outbox WHERE sequence IS NOT NULL"));
    }
}
