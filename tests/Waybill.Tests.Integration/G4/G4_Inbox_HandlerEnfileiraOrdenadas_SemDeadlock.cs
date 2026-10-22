using Microsoft.Extensions.DependencyInjection;
using Waybill.EntityFrameworkCore;
using Waybill.Tests.Integration.G3;

namespace Waybill.Tests.Integration.G4;

// A handler that enqueues ordered messages takes, in one transaction, the inbox record, its key's inbox_keys row, then
// the outbox_keys rows of what it enqueues, sorted by the numbering trigger. Every transaction takes them in that order
// and none holds two inbox_keys rows, so no cycle exists: this is a regression guard. Handlers run concurrently over
// overlapping inbox keys and outbox keys, each enqueuing in one SaveChanges, with no shared application row (that one
// is the application's own deadlock, ADR 0008). The execution strategy does not retry, so a 40P01, which Npgsql treats
// as transient, would surface instead of being retried away.
[Collection(PostgresCollection.Name)]
public sealed class G4_Inbox_HandlerEnfileiraOrdenadas_SemDeadlock(PostgresFixture postgres)
{
    private const int Rounds = 5;
    private const int Concurrent = 16;

    [Fact]
    public async Task G4_Inbox_HandlersConcorrentesEnfileirandoChavesSobrepostas_SemErro()
    {
        var ct = TestContext.Current.CancellationToken;
        var database = await TestDatabase.CreateAsync(postgres);
        await KeyLockHarness.EnableOrderingAsync(database);
        await using var services = InboxOrderHarness.Services(database, out _);
        string[] outboxKeys = ["a", "b", "c", "d", "e"];
        var results = new List<OrderedInboxResult>();

        for (var round = 0; round < Rounds; round++)
        {
            results.AddRange(await Task.WhenAll(Enumerable.Range(0, Concurrent).Select(i => Task.Run(async () =>
            {
                var random = new Random(round * Concurrent + i);
                await using var scope = services.CreateAsyncScope();
                var outbox = scope.ServiceProvider.GetRequiredService<IOutbox<AppDbContext>>();
                return await scope.ServiceProvider.GetRequiredService<IInbox<AppDbContext>>().ProcessInOrderAsync(
                    InboxOrderHarness.Handler, Guid.CreateVersion7(), new MessageOrder($"order-{i % 4}", round * Concurrent + i + 1), async (db, token) =>
                    {
                        await InboxHarness.ApplyEffect(db, token);
                        foreach (var key in random.GetItems(outboxKeys, 3))
                            outbox.Enqueue(new InvoicePaid(Guid.NewGuid(), 1m), key);
                    }, ct);
            }, ct))).WaitAsync(TimeSpan.FromSeconds(60), ct));
        }

        var applied = results.Count(r => r == OrderedInboxResult.Processed);
        Assert.True(applied > 0);
        Assert.Equal(applied, await database.EffectsAsync());
        Assert.Equal(3 * applied, await database.OutboxCountAsync());
    }
}
