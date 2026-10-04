using Npgsql;

namespace Waybill.Tests.Integration.Retencao;

// The retention counts from publication, not creation. A message that waited in the outbox (broker down for weeks)
// keeps its old UUIDv7 and created_at; the id bound narrows the search but must not decide what is deleted (ADR 0004).
[Collection(PostgresCollection.Name)]
public sealed class Retencao_Outbox_PublicadaTarde_ContaDoPublishedAt(PostgresFixture postgres)
{
    [Fact]
    public async Task Retencao_Outbox_PublicadaTarde_FicaAtePublishedAtPassarDaRetencao()
    {
        var ct = TestContext.Current.CancellationToken;
        var database = await TestDatabase.CreateAsync(postgres);
        var publishedLate = await database.InsertOutboxAsync("published", createdAgo: TimeSpan.FromDays(30), publishedAgo: TimeSpan.FromDays(1));
        var publishedLong = await database.InsertOutboxAsync("published", createdAgo: TimeSpan.FromDays(30), publishedAgo: TimeSpan.FromDays(8));
        await using var dataSource = NpgsqlDataSource.Create(database.ConnectionString);
        var cleaner = RetentionHarness.Create(dataSource, RetentionHarness.Options(database, o => o.OutboxRetention = TimeSpan.FromDays(7)));

        var pass = await cleaner.RunOnceAsync(ct);

        Assert.Equal(1, pass.OutboxDeleted);
        Assert.Equal([publishedLate], await database.OutboxIdsAsync());
        Assert.DoesNotContain(publishedLong, await database.OutboxIdsAsync());
    }
}
