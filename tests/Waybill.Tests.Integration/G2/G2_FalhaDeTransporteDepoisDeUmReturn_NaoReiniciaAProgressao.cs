using Npgsql;

namespace Waybill.Tests.Integration.G2;

// A transport failure after a basic.return spends no attempt and reopens the row at once, and the next return keeps
// the progression: it waits twice the first wait, not the first wait again.
[Collection(PostgresCollection.Name)]
public sealed class G2_FalhaDeTransporteDepoisDeUmReturn_NaoReiniciaAProgressao(PostgresFixture postgres)
{
    [Fact]
    public async Task G2_FalhaDeTransporteDepoisDeUmReturn_NaoReiniciaAProgressao_SegundaEsperaDobra()
    {
        var ct = TestContext.Current.CancellationToken;
        var database = await TestDatabase.CreateAsync(postgres);
        var id = (await DispatcherHarness.EnqueueAsync(database, 1))[0];
        await using var dataSource = NpgsqlDataSource.Create(database.ConnectionString);
        var transport = new FakeTransport((call, batch, _) => Task.FromResult<IReadOnlyList<PublishResult>>(
            batch.Select(_ => call == 2
                ? PublishResult.RetryAfter(TransportFailure.ConfirmTimeout, "no confirmation") // halves the batch, breaker stays closed
                : PublishResult.Returned("312 NO_ROUTE")).ToList()));
        var dispatcher = DispatcherHarness.Create(dataSource, transport, DispatcherHarness.Options(database, o =>
        {
            o.ReturnBackoff = TimeSpan.FromSeconds(100);
            o.MaxReturnBackoff = TimeSpan.FromSeconds(1000);
        }));

        await dispatcher.RunOnceAsync(ct);
        Assert.InRange(await database.ReturnWaitSecondsAsync(id), 95, 100);
        Assert.Equal(1, await database.SkipReturnWaitsAsync());

        await dispatcher.RunOnceAsync(ct); // transport failure
        Assert.Equal(1, await database.ScalarAsync("SELECT attempts FROM waybill.outbox"));
        Assert.Equal(0, await database.SkipReturnWaitsAsync()); // nothing to skip: claimable at once

        await dispatcher.RunOnceAsync(ct);
        Assert.Equal(2, await database.ScalarAsync("SELECT attempts FROM waybill.outbox"));
        Assert.InRange(await database.ReturnWaitSecondsAsync(id), 195, 200);
        Assert.Equal(3, transport.Calls);
    }
}
