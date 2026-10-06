using Npgsql;

namespace Waybill.Tests.Integration.Retencao;

// Retention deletes only what was delivered: published rows past the retention. A pending, claimed or dead-lettered
// row is never deleted, however old (G2: every persisted event is published or stays in the DLQ with its reason).
[Collection(PostgresCollection.Name)]
public sealed class Retencao_Outbox_ApagaSoPublicadasAlemDaRetencao(PostgresFixture postgres)
{
    [Fact]
    public async Task Retencao_Outbox_ApagaSoPublicadasAlemDaRetencao_PendenteReivindicadaEDlqFicam()
    {
        var ct = TestContext.Current.CancellationToken;
        var database = await TestDatabase.CreateAsync(postgres);
        var expired = await database.InsertOutboxAsync("published", createdAgo: TimeSpan.FromDays(8), publishedAgo: TimeSpan.FromDays(8));
        var recent = await database.InsertOutboxAsync("published", createdAgo: TimeSpan.FromDays(6), publishedAgo: TimeSpan.FromDays(6));
        var pending = await database.InsertOutboxAsync("pending", createdAgo: TimeSpan.FromDays(30));
        var claimed = await database.InsertOutboxAsync("claimed", createdAgo: TimeSpan.FromDays(30));
        var deadLettered = await database.InsertOutboxAsync("dlq", createdAgo: TimeSpan.FromDays(30));
        await using var dataSource = NpgsqlDataSource.Create(database.ConnectionString);
        var cleaner = RetentionHarness.Create(dataSource, RetentionHarness.Options(database, o => o.OutboxRetention = TimeSpan.FromDays(7)));

        var pass = await cleaner.RunOnceAsync(ct);

        Assert.Equal(1, pass.OutboxDeleted);
        var remaining = await database.OutboxIdsAsync();
        Assert.DoesNotContain(expired, remaining);
        Assert.Equal(new[] { recent, pending, claimed, deadLettered }.Order(), remaining.Order());
    }
}
