using Npgsql;

namespace Waybill.Tests.Integration.Retencao;

// The inbox forgets a message only after the retention, counted from processing. The message_id is not a UUIDv7 here
// on purpose: it may come from another system, so the age cannot be read from it (ADR 0004).
[Collection(PostgresCollection.Name)]
public sealed class Retencao_Inbox_ApagaSoExpiradas(PostgresFixture postgres)
{
    [Fact]
    public async Task Retencao_Inbox_ApagaSoAsProcessadasAlemDaRetencao()
    {
        var ct = TestContext.Current.CancellationToken;
        var database = await TestDatabase.CreateAsync(postgres);
        var expired = Guid.NewGuid();
        var recent = Guid.NewGuid();
        await database.InsertInboxAsync("billing.mark-paid", expired, processedAgo: TimeSpan.FromDays(31));
        await database.InsertInboxAsync("billing.mark-paid", recent, processedAgo: TimeSpan.FromDays(29));
        await database.InsertInboxAsync("audit.record", expired, processedAgo: TimeSpan.FromDays(29)); // same message, another handler
        await using var dataSource = NpgsqlDataSource.Create(database.ConnectionString);
        var cleaner = RetentionHarness.Create(dataSource, RetentionHarness.Options(database, o => o.InboxRetention = TimeSpan.FromDays(30)));

        var pass = await cleaner.RunOnceAsync(ct);

        Assert.Equal(1, pass.InboxDeleted);
        Assert.Equal(0, await database.ScalarAsync($"SELECT count(*) FROM waybill.inbox WHERE handler = 'billing.mark-paid' AND message_id = '{expired}'"));
        Assert.Equal(1, await database.ScalarAsync($"SELECT count(*) FROM waybill.inbox WHERE handler = 'billing.mark-paid' AND message_id = '{recent}'"));
        Assert.Equal(1, await database.ScalarAsync($"SELECT count(*) FROM waybill.inbox WHERE handler = 'audit.record' AND message_id = '{expired}'"));
    }
}
