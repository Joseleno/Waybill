using Npgsql;
using Waybill.EntityFrameworkCore.Dispatching;

namespace Waybill.Tests.Integration.G2;

// The most common fencing case in production: one owner whose lease ran out (GC pause, slow broker) claims the same
// rows again. Only the fence tells the two cycles apart, so the old cycle's late marking must touch nothing.
[Collection(PostgresCollection.Name)]
public sealed class G2_MesmaInstanciaReivindicaDeNovo_FenceBarraMarcacaoAntiga(PostgresFixture postgres)
{
    [Fact]
    public async Task G2_MesmaInstanciaReivindicaDeNovo_FenceBarraMarcacaoEDevolucaoAntigas()
    {
        var ct = TestContext.Current.CancellationToken;
        var database = await TestDatabase.CreateAsync(postgres);
        await DispatcherHarness.EnqueueAsync(database, 4);
        await using var dataSource = NpgsqlDataSource.Create(database.ConnectionString);
        var store = new OutboxStore(dataSource);
        const string owner = "pod-a/1/same";

        var oldCycle = await store.ClaimAsync(owner, 10, TimeSpan.FromSeconds(30), ct);
        await database.ScalarAsync("WITH u AS (UPDATE waybill.outbox SET lease_until = clock_timestamp() - interval '1 second' RETURNING 1) SELECT count(*) FROM u");
        var newCycle = await store.ClaimAsync(owner, 10, TimeSpan.FromSeconds(30), ct);

        Assert.All(oldCycle, c => Assert.Equal(1, c.Fence));
        Assert.All(newCycle, c => Assert.Equal(2, c.Fence));

        Assert.Equal(4, await store.FinishAsync(owner, oldCycle.Select(c => (c, PublishResult.Confirmed)).ToList(), 5, ct));
        Assert.Equal(4, await store.FinishAsync(owner, oldCycle.Select(c => (c, PublishResult.Retry)).ToList(), 5, ct));
        Assert.Equal(4, await database.CountAsync("claimed")); // the new cycle still holds them

        Assert.Equal(0, await store.FinishAsync(owner, newCycle.Select(c => (c, PublishResult.Confirmed)).ToList(), 5, ct));
        Assert.Equal(4, await database.CountAsync("published"));
    }
}
