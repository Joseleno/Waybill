using Npgsql;
using Waybill.EntityFrameworkCore;
using Waybill.Tests.Integration.G3;

namespace Waybill.Tests.Integration.Retencao;

// Not a guarantee: the boundary of G3. Once retention deletes the inbox row, a redelivery of that message is processed
// again. ProcessAsync does not refuse old messages (ADR 0004): refusing by the UUIDv7's age would drop the first
// delivery of a message that waited in the outbox while the broker was down. OPERATIONS.md says how to size the
// inbox retention so this window is never reached.
[Collection(PostgresCollection.Name)]
public sealed class Inbox_ReentregaDepoisDaLimpeza_ProcessaDeNovo(PostgresFixture postgres)
{
    [Fact]
    public async Task Inbox_ReentregaDentroDaRetencaoEhDuplicata_DepoisDaLimpezaProcessaDeNovo()
    {
        var ct = TestContext.Current.CancellationToken;
        var database = await TestDatabase.CreateAsync(postgres);
        await using var services = database.Services();
        var messageId = Guid.CreateVersion7();
        await using var dataSource = NpgsqlDataSource.Create(database.ConnectionString);
        var cleaner = RetentionHarness.Create(dataSource, RetentionHarness.Options(database, o => o.InboxRetention = TimeSpan.FromDays(30)));

        Assert.Equal(InboxResult.Processed, await InboxHarness.DeliverAsync(services, "billing.mark-paid", messageId));
        await cleaner.RunOnceAsync(ct);
        Assert.Equal(InboxResult.Duplicate, await InboxHarness.DeliverAsync(services, "billing.mark-paid", messageId));

        await using (var age = dataSource.CreateCommand("UPDATE waybill.inbox SET processed_at = processed_at - interval '31 days'"))
            await age.ExecuteNonQueryAsync(ct);
        Assert.Equal(1, (await cleaner.RunOnceAsync(ct)).InboxDeleted);

        Assert.Equal(InboxResult.Processed, await InboxHarness.DeliverAsync(services, "billing.mark-paid", messageId));
        Assert.Equal(2, await database.EffectsAsync());
    }
}
