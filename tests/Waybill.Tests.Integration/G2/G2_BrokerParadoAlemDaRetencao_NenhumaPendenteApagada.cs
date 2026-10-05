using Npgsql;
using Waybill.Tests.Integration.Retencao;

namespace Waybill.Tests.Integration.G2;

// G2 with retention on: the broker stays down for longer than the outbox retention while cleanup keeps running.
// Nothing still to be published (pending, claimed by a dead instance) and nothing in the DLQ is deleted; only rows
// already published before the outage go. When the broker comes back, everything pending is published.
[Collection(PostgresCollection.Name)]
public sealed class G2_BrokerParadoAlemDaRetencao_NenhumaPendenteApagada(PostgresFixture postgres)
{
    private static readonly TimeSpan Retention = TimeSpan.FromSeconds(1);

    [Fact]
    public async Task G2_BrokerParadoAlemDaRetencao_SoAsJaPublicadasSomemEOBacklogPublicaQuandoVolta()
    {
        var ct = TestContext.Current.CancellationToken;
        var database = await TestDatabase.CreateAsync(postgres);
        var publishedBefore = await database.InsertOutboxAsync("published", createdAgo: TimeSpan.FromSeconds(5), publishedAgo: TimeSpan.FromSeconds(5));
        var pending = await DispatcherHarness.EnqueueAsync(database, 20);
        var claimedByDeadInstance = await database.InsertOutboxAsync("claimed", createdAgo: TimeSpan.FromSeconds(5));
        var deadLettered = await database.InsertOutboxAsync("dlq", createdAgo: TimeSpan.FromSeconds(5));
        await using var dataSource = NpgsqlDataSource.Create(database.ConnectionString);
        var brokerDown = new FakeTransport((_, batch, _) =>
            Task.FromResult<IReadOnlyList<PublishResult>>(batch.Select(_ => PublishResult.RetryAfter(TransportFailure.Connection, "broker down")).ToList()));
        var dispatcher = DispatcherHarness.Create(dataSource, brokerDown, DispatcherHarness.Options(database));
        var cleaner = RetentionHarness.Create(dataSource, RetentionHarness.Options(database, o => o.OutboxRetention = Retention));

        // The outage outlasts the retention three times over, with the dispatcher failing and cleanup running throughout.
        var outageEnds = DateTime.UtcNow + 3 * Retention;
        while (DateTime.UtcNow < outageEnds)
        {
            await dispatcher.RunOnceAsync(ct);
            await cleaner.RunOnceAsync(ct);
            await Task.Delay(100, ct);
        }

        Assert.True(brokerDown.Calls > 0);
        var remaining = await database.OutboxIdsAsync();
        Assert.DoesNotContain(publishedBefore, remaining);
        Assert.Equal(pending.Append(claimedByDeadInstance).Append(deadLettered).Order(), remaining.Order());
        Assert.Equal(0, await database.ScalarAsync("SELECT count(*) FROM waybill.outbox WHERE attempts > 0 AND status <> 'dlq'"));

        var brokerBack = new FakeTransport();
        var recovered = DispatcherHarness.Create(dataSource, brokerBack, DispatcherHarness.Options(database));
        while (await database.ScalarAsync("SELECT count(*) FROM waybill.outbox WHERE status IN ('pending', 'claimed')") > 0)
            await recovered.RunOnceAsync(ct);

        Assert.Equal(pending.Append(claimedByDeadInstance).Order(), brokerBack.Received.Select(m => m.MessageId).Distinct().Order());
        Assert.Equal(21, await database.CountAsync("published"));
        Assert.Equal(1, await database.CountAsync("dlq"));
    }
}
