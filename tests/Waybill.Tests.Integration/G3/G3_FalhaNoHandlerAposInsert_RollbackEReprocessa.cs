using Waybill.EntityFrameworkCore;

namespace Waybill.Tests.Integration.G3;

// A handler that fails after the inbox INSERT rolls everything back, the inbox row included, so the redelivery is
// processed rather than taken for a duplicate.
[Collection(PostgresCollection.Name)]
public sealed class G3_FalhaNoHandlerAposInsert_RollbackEReprocessa(PostgresFixture postgres)
{
    [Fact]
    public async Task G3_FalhaNoHandlerAposInsert_RollbackCompletoEReentregaProcessa()
    {
        var database = await TestDatabase.CreateAsync(postgres);
        await using var services = database.Services();
        var messageId = Guid.CreateVersion7();

        await Assert.ThrowsAsync<InvalidOperationException>(() => InboxHarness.DeliverAsync(services, "billing.mark-paid", messageId,
            async (db, ct) =>
            {
                await InboxHarness.ApplyEffect(db, ct);
                await db.SaveChangesAsync(ct); // even an effect already flushed inside the transaction is rolled back
                throw new InvalidOperationException("invoice service unavailable");
            }));
        Assert.Equal(0, await database.EffectsAsync());
        Assert.Equal(0, await database.InboxRowsAsync());

        Assert.Equal(InboxResult.Processed, await InboxHarness.DeliverAsync(services, "billing.mark-paid", messageId));
        Assert.Equal(1, await database.EffectsAsync());
    }
}
