using Npgsql;

namespace Waybill.Tests.Integration.G2;

// basic.return (unroutable) has its own attempt budget: each return spends one, the last one sends the message to
// the DLQ with the broker's reason. Unlike a transport failure, it is not retried forever.
[Collection(PostgresCollection.Name)]
public sealed class G2_Returned_OrcamentoProprioDepoisDlq(PostgresFixture postgres)
{
    [Fact]
    public async Task G2_Returned_OrcamentoProprioDepoisDlq_ComMotivo()
    {
        var ct = TestContext.Current.CancellationToken;
        var database = await TestDatabase.CreateAsync(postgres);
        await DispatcherHarness.EnqueueAsync(database, 1);
        await using var dataSource = NpgsqlDataSource.Create(database.ConnectionString);
        var transport = new FakeTransport((_, batch, _) => Task.FromResult<IReadOnlyList<PublishResult>>(
            batch.Select(_ => new PublishResult(PublishStatus.Returned, "312 NO_ROUTE")).ToList()));
        var dispatcher = DispatcherHarness.Create(dataSource, transport, DispatcherHarness.Options(database, o => o.MaxReturns = 3));

        for (var attempt = 1; attempt <= 2; attempt++)
        {
            await dispatcher.RunOnceAsync(ct);
            Assert.Equal(attempt, await database.ScalarAsync("SELECT attempts FROM waybill.outbox"));
            Assert.Equal(1, await database.CountAsync("pending"));
        }

        await dispatcher.RunOnceAsync(ct);
        Assert.Equal(1, await database.ScalarAsync("SELECT count(*) FROM waybill.outbox WHERE status = 'dlq' AND attempts = 3 AND dlq_reason = '312 NO_ROUTE'"));
        Assert.Equal(0, await dispatcher.RunOnceAsync(ct)); // the DLQ is not claimed again
        Assert.Equal(3, transport.Calls);
    }
}
