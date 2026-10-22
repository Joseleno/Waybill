using Npgsql;

namespace Waybill.Tests.Integration.G2;

// After the k-th basic.return a message waits min(ReturnBackoff × 2^(k−1), MaxReturnBackoff) before it is claimed again,
// by the database clock; the MaxReturns-th return sends it to the DLQ. The missing binding gets minutes to appear, not
// the ~5 s of back-to-back cycles (ADR 0006).
[Collection(PostgresCollection.Name)]
public sealed class G2_Returned_EspacamentoCrescenteAteOTeto(PostgresFixture postgres)
{
    [Fact]
    public async Task G2_Returned_EspacamentoCrescenteAteOTeto_DepoisDlq()
    {
        var ct = TestContext.Current.CancellationToken;
        var database = await TestDatabase.CreateAsync(postgres);
        var id = (await DispatcherHarness.EnqueueAsync(database, 1))[0];
        await using var dataSource = NpgsqlDataSource.Create(database.ConnectionString);
        var transport = new FakeTransport((_, batch, _) => Task.FromResult<IReadOnlyList<PublishResult>>(
            batch.Select(_ => PublishResult.Returned("312 NO_ROUTE")).ToList()));
        var dispatcher = DispatcherHarness.Create(dataSource, transport, DispatcherHarness.Options(database, o =>
        {
            o.MaxReturns = 5;
            o.ReturnBackoff = TimeSpan.FromSeconds(100);
            o.MaxReturnBackoff = TimeSpan.FromSeconds(300);
        }));

        foreach (var expected in new[] { 100, 200, 300, 300 }) // 400 is above the ceiling
        {
            Assert.Equal(1, (await dispatcher.RunOnceAsync(ct)).Claimed);
            var wait = await database.ReturnWaitSecondsAsync(id);
            Assert.InRange(wait, expected - 5, expected);
            Assert.Equal(0, (await dispatcher.RunOnceAsync(ct)).Claimed); // still waiting
            Assert.Equal(1, await database.SkipReturnWaitsAsync());
        }

        Assert.Equal(1, (await dispatcher.RunOnceAsync(ct)).Claimed);
        Assert.Equal(1, await database.ScalarAsync("SELECT count(*) FROM waybill.outbox WHERE status = 'dlq' AND attempts = 5 AND dlq_reason = '312 NO_ROUTE' AND next_attempt_at IS NULL"));
        Assert.Equal(5, transport.Calls);
    }
}
