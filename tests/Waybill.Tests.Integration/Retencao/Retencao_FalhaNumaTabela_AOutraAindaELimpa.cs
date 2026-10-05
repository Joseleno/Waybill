using Microsoft.Extensions.Logging;
using Npgsql;

namespace Waybill.Tests.Integration.Retencao;

// The outbox and the inbox are cleaned independently: a statement that keeps failing on one table (a permission, a
// broken migration) is logged and does not leave the other one growing forever (review finding).
[Collection(PostgresCollection.Name)]
public sealed class Retencao_FalhaNumaTabela_AOutraAindaELimpa(PostgresFixture postgres)
{
    [Fact]
    public async Task Retencao_OutboxInacessivel_LogaErroELimpaOInbox()
    {
        var ct = TestContext.Current.CancellationToken;
        var database = await TestDatabase.CreateAsync(postgres);
        await database.InsertInboxBulkAsync(5, TimeSpan.FromDays(40));
        await using var dataSource = NpgsqlDataSource.Create(database.ConnectionString);
        await using (var hide = dataSource.CreateCommand("ALTER TABLE waybill.outbox RENAME TO outbox_hidden"))
            await hide.ExecuteNonQueryAsync(ct);
        var logs = new LogSink();
        var cleaner = RetentionHarness.Create(dataSource, RetentionHarness.Options(database), logs);

        var pass = await cleaner.RunOnceAsync(ct);

        Assert.Equal(5, pass.InboxDeleted);
        Assert.Contains(logs.Entries, e => e.Level == LogLevel.Error && e.Message.Contains("outbox"));
    }
}
