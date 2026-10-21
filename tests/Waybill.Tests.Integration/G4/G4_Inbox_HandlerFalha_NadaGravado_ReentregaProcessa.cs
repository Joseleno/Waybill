using Microsoft.Extensions.DependencyInjection;
using Waybill.EntityFrameworkCore;
using Waybill.Tests.Integration.G3;

namespace Waybill.Tests.Integration.G4;

// A handler that throws rolls everything back, the key's move included: the redelivery of the same message is applied,
// not refused as already seen.
[Collection(PostgresCollection.Name)]
public sealed class G4_Inbox_HandlerFalha_NadaGravado_ReentregaProcessa(PostgresFixture postgres)
{
    [Fact]
    public async Task G4_Inbox_HandlerLanca_ChaveNaoAndaEReentregaAplica()
    {
        var database = await TestDatabase.CreateAsync(postgres);
        await using var services = InboxOrderHarness.Services(database, out _);
        await InboxOrderHarness.DeliverAsync(services, Guid.CreateVersion7(), 1);
        var messageId = Guid.CreateVersion7();

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await using var scope = services.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<IInbox<AppDbContext>>().ProcessInOrderAsync(
                InboxOrderHarness.Handler, messageId, new MessageOrder(InboxOrderHarness.Key, 2), async (db, ct) =>
                {
                    await InboxHarness.ApplyEffect(db, ct);
                    throw new InvalidOperationException("the handler failed");
                }, TestContext.Current.CancellationToken);
        });

        Assert.Equal(1, await database.EffectsAsync());
        Assert.Equal(1, await database.InboxRowsAsync());
        Assert.Equal(1, (await InboxOrderHarness.LastAppliedAsync(database))!.Value.Sequence);

        Assert.Equal(OrderedInboxResult.Processed, await InboxOrderHarness.DeliverAsync(services, messageId, 2));
        Assert.Equal((2L, messageId), await InboxOrderHarness.LastAppliedAsync(database));
    }
}
