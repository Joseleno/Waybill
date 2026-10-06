using Npgsql;

namespace Waybill.Tests.Integration.G2;

// With a large MaxReturns, ReturnBackoff × 2^k would overflow PostgreSQL's interval long before the ceiling applies.
// The exponent is bounded first, so a message deep into its budget still gets a wait at the ceiling, not an error that
// would fail the whole batch's bookkeeping.
[Collection(PostgresCollection.Name)]
public sealed class G2_Returned_MuitasTentativas_IntervaloNaoEstoura(PostgresFixture postgres)
{
    [Fact]
    public async Task G2_Returned_MuitasTentativas_IntervaloNaoEstoura_EsperaNoTeto()
    {
        var ct = TestContext.Current.CancellationToken;
        var database = await TestDatabase.CreateAsync(postgres);
        var id = (await DispatcherHarness.EnqueueAsync(database, 1))[0];
        await database.ScalarAsync("UPDATE waybill.outbox SET attempts = 998"); // 998 returns already spent
        await using var dataSource = NpgsqlDataSource.Create(database.ConnectionString);
        var transport = new FakeTransport((_, batch, _) => Task.FromResult<IReadOnlyList<PublishResult>>(
            batch.Select(_ => PublishResult.Returned("312 NO_ROUTE")).ToList()));
        var dispatcher = DispatcherHarness.Create(dataSource, transport, DispatcherHarness.Options(database, o =>
        {
            o.MaxReturns = 1000;
            o.ReturnBackoff = TimeSpan.FromDays(1);
            o.MaxReturnBackoff = TimeSpan.FromDays(1);
        }));

        await dispatcher.RunOnceAsync(ct);

        Assert.Equal(999, await database.ScalarAsync("SELECT attempts FROM waybill.outbox"));
        Assert.InRange(await database.ReturnWaitSecondsAsync(id), 86_400 - 5, 86_400);
    }
}
