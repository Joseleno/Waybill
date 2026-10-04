using Npgsql;

namespace Waybill.Tests.Integration.G2;

// G2: a transport failure hands the batch back at once and spends no attempt; the broker being down never moves
// a message toward the DLQ.
[Collection(PostgresCollection.Name)]
public sealed class G2_FalhaDeTransporte_ReabreSemGastarTentativa(PostgresFixture postgres)
{
    [Theory]
    [InlineData("exception")]
    [InlineData("retry")]
    public async Task G2_FalhaDeTransporte_ReabreSemGastarTentativa_DepoisPublica(string failure)
    {
        var ct = TestContext.Current.CancellationToken;
        var database = await TestDatabase.CreateAsync(postgres);
        await DispatcherHarness.EnqueueAsync(database, 3);
        await using var dataSource = NpgsqlDataSource.Create(database.ConnectionString);
        var transport = new FakeTransport((call, batch, _) => call switch
        {
            <= 2 when failure == "exception" => throw new IOException("connection reset by broker"),
            <= 2 => Task.FromResult<IReadOnlyList<PublishResult>>(batch.Select(_ => PublishResult.Retry).ToList()),
            _ => FakeTransport.ConfirmAll(batch),
        });
        var dispatcher = DispatcherHarness.Create(dataSource, transport, DispatcherHarness.Options(database));

        for (var cycle = 0; cycle < 2; cycle++)
        {
            Assert.Equal(3, (await dispatcher.RunOnceAsync(ct)).Claimed); // handed back at once, so claimable again right away
            Assert.Equal(3, await database.ScalarAsync(
                "SELECT count(*) FROM waybill.outbox WHERE status = 'pending' AND attempts = 0 AND lease_until IS NULL"));
        }

        Assert.Equal(3, (await dispatcher.RunOnceAsync(ct)).Claimed);
        Assert.Equal(3, await database.CountAsync("published"));
        Assert.Equal(0, await database.ScalarAsync("SELECT coalesce(max(attempts), 0) FROM waybill.outbox"));
        Assert.Equal(3, await database.ScalarAsync("SELECT max(fence) FROM waybill.outbox"));
    }

    // A buggy transport returning default(PublishResult) or an undefined status must never mark a message as
    // published without a broker confirmation: it is a transport failure.
    [Theory]
    [InlineData(0)]
    [InlineData(99)]
    public async Task G2_ResultadoInvalidoDoTransporte_NuncaContaComoConfirmado(int status)
    {
        var ct = TestContext.Current.CancellationToken;
        var database = await TestDatabase.CreateAsync(postgres);
        await DispatcherHarness.EnqueueAsync(database, 3);
        await using var dataSource = NpgsqlDataSource.Create(database.ConnectionString);
        var transport = new FakeTransport((_, batch, _) => Task.FromResult<IReadOnlyList<PublishResult>>(
            batch.Select(_ => new PublishResult((PublishStatus)status)).ToList()));
        var dispatcher = DispatcherHarness.Create(dataSource, transport, DispatcherHarness.Options(database));

        await dispatcher.RunOnceAsync(ct);

        Assert.Equal(0, await database.CountAsync("published"));
        Assert.Equal(3, await database.ScalarAsync("SELECT count(*) FROM waybill.outbox WHERE status = 'pending' AND attempts = 0"));
    }
}
