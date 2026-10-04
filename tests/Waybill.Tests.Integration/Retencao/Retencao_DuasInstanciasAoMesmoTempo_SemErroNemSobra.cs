using Npgsql;

namespace Waybill.Tests.Integration.Retencao;

// Every instance of the application may run the cleanup. Two at once must not fail, must not leave eligible rows
// behind and must not touch ineligible ones. That SKIP LOCKED keeps one from waiting on the other is proven apart,
// with rows held by an open transaction: here, a cleaner that waited would still finish.
[Collection(PostgresCollection.Name)]
public sealed class Retencao_DuasInstanciasAoMesmoTempo_SemErroNemSobra(PostgresFixture postgres)
{
    private const int Expired = 5000;

    [Fact]
    public async Task Retencao_DuasInstanciasAoMesmoTempo_ApagamCadaLinhaElegivelUmaVez()
    {
        var ct = TestContext.Current.CancellationToken;
        var database = await TestDatabase.CreateAsync(postgres);
        await database.InsertPublishedOutboxBulkAsync(Expired, TimeSpan.FromDays(40));
        await database.InsertInboxBulkAsync(Expired, TimeSpan.FromDays(40));
        var kept = new[]
        {
            await database.InsertOutboxAsync("pending", createdAgo: TimeSpan.FromDays(40)),
            await database.InsertOutboxAsync("claimed", createdAgo: TimeSpan.FromDays(40)),
            await database.InsertOutboxAsync("dlq", createdAgo: TimeSpan.FromDays(40)),
            await database.InsertOutboxAsync("published", createdAgo: TimeSpan.FromDays(1), publishedAgo: TimeSpan.FromDays(1)),
        };
        await database.InsertInboxAsync("recent", Guid.NewGuid(), processedAgo: TimeSpan.FromDays(1));
        await using var dataSource = NpgsqlDataSource.Create(database.ConnectionString);
        var options = RetentionHarness.Options(database, o => o.BatchSize = 20);
        var first = RetentionHarness.Create(dataSource, options);
        var second = RetentionHarness.Create(dataSource, options);

        var passes = await Task.WhenAll(Task.Run(() => first.RunOnceAsync(ct), ct), Task.Run(() => second.RunOnceAsync(ct), ct));

        Assert.Equal(Expired, passes.Sum(p => p.OutboxDeleted));
        Assert.Equal(Expired, passes.Sum(p => p.InboxDeleted));
        Assert.Equal(kept.Order(), (await database.OutboxIdsAsync()).Order());
        Assert.Equal(1, await database.ScalarAsync("SELECT count(*) FROM waybill.inbox WHERE handler = 'recent'"));
        Assert.Equal(1, await database.ScalarAsync("SELECT count(*) FROM waybill.inbox"));
    }
}
