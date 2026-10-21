using Microsoft.Extensions.DependencyInjection;
using Waybill.EntityFrameworkCore;
using Waybill.Tests.Integration.G3;

namespace Waybill.Tests.Integration.G4;

// The first delivery of a new key (5) fails in its handler while another of the same key, with a lower sequence (4),
// waits on its insert. The first rolls back, so its sequence was never applied: the waiting one is applied, and the
// key ends at 4, not at a 5 that never happened.
[Collection(PostgresCollection.Name)]
public sealed class G4_Inbox_PrimeiraDaChaveDesfeita_ASegundaAplica(PostgresFixture postgres)
{
    [Fact]
    public async Task G4_Inbox_PrimeiraDeChaveNovaFalha_ASegundaMenorAplica()
    {
        var ct = TestContext.Current.CancellationToken;
        var database = await TestDatabase.CreateAsync(postgres);
        await using var services = InboxOrderHarness.Services(database, out _);
        var (entered, release) = (new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously), new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));

        var failing = Task.Run(async () =>
        {
            await using var scope = services.CreateAsyncScope();
            return await scope.ServiceProvider.GetRequiredService<IInbox<AppDbContext>>().ProcessInOrderAsync(
                InboxOrderHarness.Handler, Guid.CreateVersion7(), new MessageOrder(InboxOrderHarness.Key, 5), async (db, token) =>
                {
                    entered.TrySetResult();
                    await release.Task.WaitAsync(token);
                    throw new InvalidOperationException("the handler failed");
                });
        }, ct);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), ct);
        var (lower, calls) = (Guid.CreateVersion7(), new HandlerCalls());
        var waiting = InboxOrderHarness.DeliverAsync(services, lower, 4, calls);
        await KeyLockHarness.WaitForAsync(database, atBarrier: 0, onLocks: 1);
        Assert.Equal(0, calls.Count);

        release.SetResult();

        await Assert.ThrowsAsync<InvalidOperationException>(() => failing.WaitAsync(TimeSpan.FromSeconds(10), ct));
        Assert.Equal(OrderedInboxResult.Processed, await waiting.WaitAsync(TimeSpan.FromSeconds(10), ct));
        Assert.Equal((4L, lower), await InboxOrderHarness.LastAppliedAsync(database));
        Assert.Equal(1, await database.EffectsAsync());
    }
}
