using Microsoft.Extensions.DependencyInjection;
using Waybill.EntityFrameworkCore;

namespace Waybill.Tests.Integration.G3;

// A consumer that also produces (choreography): the event it enqueues commits with its effect and its inbox row, or
// none of them does.
[Collection(PostgresCollection.Name)]
public sealed class G3_HandlerEnfileiraEFalha_SemInboxNemOutbox(PostgresFixture postgres)
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task G3_HandlerEnfileiraEFalha_SemInboxNemOutbox_OuTudoNoMesmoCommit(bool handlerFails)
    {
        var database = await TestDatabase.CreateAsync(postgres);
        await using var services = database.Services();
        var messageId = Guid.CreateVersion7();

        await using (var scope = services.CreateAsyncScope())
        {
            var inbox = scope.ServiceProvider.GetRequiredService<IInbox<AppDbContext>>();
            var outbox = scope.ServiceProvider.GetRequiredService<IOutbox<AppDbContext>>();
            var processing = inbox.ProcessAsync("billing.mark-paid", messageId, async (db, ct) =>
            {
                await InboxHarness.ApplyEffect(db, ct);
                outbox.Enqueue(new AuditRecorded("receipt-issued"));
                if (handlerFails)
                    throw new InvalidOperationException("receipt template missing");
            }, TestContext.Current.CancellationToken);

            if (handlerFails)
                await Assert.ThrowsAsync<InvalidOperationException>(() => processing);
            else
                Assert.Equal(InboxResult.Processed, await processing);
        }

        var expected = handlerFails ? 0 : 1;
        Assert.Equal(expected, await database.EffectsAsync());
        Assert.Equal(expected, await database.InboxRowsAsync());
        Assert.Equal(expected, await database.ScalarAsync("SELECT count(*) FROM waybill.outbox WHERE type = 'audit.recorded.v1'"));
    }
}
