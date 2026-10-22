using System.Diagnostics;
using Npgsql;
using Waybill.EntityFrameworkCore.Dispatching;

namespace Waybill.Tests.Integration.G2;

// A hot message type without a binding puts thousands of rows in the wait after a basic.return, ahead of everything
// newer in claim order. The partial index cannot filter on the clock, so every claim reads and passes over them. The
// backlog behind them must keep flowing, and the cost of reading past them is measured here (ADR 0006).
[Collection(PostgresCollection.Name)]
public sealed class G2_DezMilEmEspera_ClaimContinuaFluindo(PostgresFixture postgres)
{
    private const int Waiting = 10_000;
    private const int Samples = 30;

    [Fact]
    public async Task G2_DezMilEmEspera_ClaimContinuaFluindo_LatenciaMedida()
    {
        var ct = TestContext.Current.CancellationToken;
        var database = await TestDatabase.CreateAsync(postgres);
        await DispatcherHarness.EnqueueAsync(database, Waiting);
        await database.ScalarAsync("UPDATE waybill.outbox SET attempts = 1, next_attempt_at = clock_timestamp() + interval '1 hour'");
        await DispatcherHarness.EnqueueAsync(database, 100);
        await database.ScalarAsync("VACUUM ANALYZE waybill.outbox");
        await using var dataSource = NpgsqlDataSource.Create(database.ConnectionString);
        var store = new OutboxStore(dataSource);

        var latencies = new List<double>(Samples);
        for (var i = 0; i < Samples; i++)
        {
            var watch = Stopwatch.StartNew();
            var claimed = await store.ClaimAsync("measuring", 100, TimeSpan.FromSeconds(30), ct);
            latencies.Add(watch.Elapsed.TotalMilliseconds);
            Assert.Equal(100, claimed.Count);
            Assert.All(claimed, c => Assert.Equal(1, c.Fence - i)); // the fresh rows, claimed once per sample
            await store.ReleaseOwnedAsync("measuring", ct);
        }

        latencies.Sort();
        TestContext.Current.TestOutputHelper?.WriteLine(
            $"claim with {Waiting} waiting rows ahead: p50 {latencies[Samples / 2]:F1} ms, max {latencies[^1]:F1} ms");
        Assert.Equal(Waiting, await database.ScalarAsync("SELECT count(*) FROM waybill.outbox WHERE status = 'pending' AND next_attempt_at > clock_timestamp()"));
    }
}
