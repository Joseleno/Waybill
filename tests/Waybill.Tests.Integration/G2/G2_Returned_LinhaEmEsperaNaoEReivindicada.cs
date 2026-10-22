using Npgsql;

namespace Waybill.Tests.Integration.G2;

// A message waiting after a basic.return is passed over by the claim while the rest of the backlog flows; once its
// wait ends it is claimed like any pending row.
[Collection(PostgresCollection.Name)]
public sealed class G2_Returned_LinhaEmEsperaNaoEReivindicada(PostgresFixture postgres)
{
    [Fact]
    public async Task G2_Returned_LinhaEmEsperaNaoEReivindicada_AsOutrasSeguem()
    {
        var ct = TestContext.Current.CancellationToken;
        var database = await TestDatabase.CreateAsync(postgres);
        var ids = await DispatcherHarness.EnqueueAsync(database, 3);
        var unroutable = ids[0];
        await using var dataSource = NpgsqlDataSource.Create(database.ConnectionString);
        var transport = new FakeTransport((call, batch, _) => Task.FromResult<IReadOnlyList<PublishResult>>(
            batch.Select(m => call == 1 && m.MessageId == unroutable ? PublishResult.Returned("312 NO_ROUTE") : PublishResult.Confirmed).ToList()));
        var dispatcher = DispatcherHarness.Create(dataSource, transport, DispatcherHarness.Options(database));

        Assert.Equal(3, (await dispatcher.RunOnceAsync(ct)).Claimed);
        Assert.Equal(2, await database.CountAsync("published"));

        await DispatcherHarness.EnqueueAsync(database, 2); // newer rows, behind the waiting one in claim order
        Assert.Equal(2, (await dispatcher.RunOnceAsync(ct)).Claimed);
        Assert.DoesNotContain(transport.Received.Skip(3), m => m.MessageId == unroutable);

        Assert.Equal(1, await database.SkipReturnWaitsAsync());
        Assert.Equal(1, (await dispatcher.RunOnceAsync(ct)).Claimed);
        Assert.Equal(5, await database.CountAsync("published"));
    }
}
