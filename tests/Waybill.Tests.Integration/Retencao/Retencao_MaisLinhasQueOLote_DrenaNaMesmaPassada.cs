using Npgsql;

namespace Waybill.Tests.Integration.Retencao;

// Each statement deletes at most BatchSize rows, so no single transaction holds many locks; one pass still repeats
// until nothing eligible is left, including an exact multiple of the batch (the last, empty batch ends the pass).
[Collection(PostgresCollection.Name)]
public sealed class Retencao_MaisLinhasQueOLote_DrenaNaMesmaPassada(PostgresFixture postgres)
{
    [Theory]
    [InlineData(16, 11)]
    [InlineData(15, 10)] // exact multiples of the batch
    public async Task Retencao_MaisLinhasQueOLote_UmaPassadaApagaTudoEmLotes(int outboxRows, int inboxRows)
    {
        var ct = TestContext.Current.CancellationToken;
        var database = await TestDatabase.CreateAsync(postgres);
        for (var i = 0; i < outboxRows; i++)
            await database.InsertOutboxAsync("published", createdAgo: TimeSpan.FromDays(40), publishedAgo: TimeSpan.FromDays(40));
        for (var i = 0; i < inboxRows; i++)
            await database.InsertInboxAsync("billing.mark-paid", Guid.NewGuid(), processedAgo: TimeSpan.FromDays(40));
        await using var dataSource = NpgsqlDataSource.Create(database.ConnectionString);
        var cleaner = RetentionHarness.Create(dataSource, RetentionHarness.Options(database, o => o.BatchSize = 5));

        var pass = await cleaner.RunOnceAsync(ct);

        Assert.Equal(new(outboxRows, inboxRows), (pass.OutboxDeleted, pass.InboxDeleted));
        Assert.Equal(0, await database.OutboxCountAsync());
        Assert.Equal(0, await database.ScalarAsync("SELECT count(*) FROM waybill.inbox"));
    }
}
