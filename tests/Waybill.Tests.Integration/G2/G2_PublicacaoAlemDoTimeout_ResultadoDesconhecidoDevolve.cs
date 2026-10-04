using System.Diagnostics;
using Npgsql;

namespace Waybill.Tests.Integration.G2;

// The publish wait is cancelled at PublishTimeout, before the lease (PublishTimeout + LeaseMargin) runs out. The
// outcome is unknown, so the batch is handed back: it may reach the broker twice, never zero times.
[Collection(PostgresCollection.Name)]
public sealed class G2_PublicacaoAlemDoTimeout_ResultadoDesconhecidoDevolve(PostgresFixture postgres)
{
    [Fact]
    public async Task G2_PublicacaoAlemDoTimeout_ResultadoDesconhecidoDevolve_AntesDoLease()
    {
        var ct = TestContext.Current.CancellationToken;
        var database = await TestDatabase.CreateAsync(postgres);
        var ids = await DispatcherHarness.EnqueueAsync(database, 2);
        await using var dataSource = NpgsqlDataSource.Create(database.ConnectionString);
        var transport = new FakeTransport(async (call, batch, token) =>
        {
            if (call == 1)
                await Task.Delay(Timeout.Infinite, token); // the confirmation never comes
            return batch.Select(_ => PublishResult.Confirmed).ToList();
        });
        var options = DispatcherHarness.Options(database, o =>
        {
            o.PublishTimeout = TimeSpan.FromMilliseconds(300);
            o.LeaseMargin = TimeSpan.FromSeconds(30);
        });
        var dispatcher = DispatcherHarness.Create(dataSource, transport, options);

        var stopwatch = Stopwatch.StartNew();
        await dispatcher.RunOnceAsync(ct);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(5), $"the publish wait was not cancelled: {stopwatch.Elapsed}");
        Assert.Equal(2, await database.ScalarAsync("SELECT count(*) FROM waybill.outbox WHERE status = 'pending' AND lease_until IS NULL"));

        await dispatcher.RunOnceAsync(ct); // long before the 30 s lease would have expired
        Assert.Equal(2, await database.CountAsync("published"));
        Assert.Equal(ids.Concat(ids).Order(), transport.Received.Select(m => m.MessageId).Order()); // each one twice
    }
}
