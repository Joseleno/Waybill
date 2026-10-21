using Microsoft.Extensions.DependencyInjection;
using Waybill.EntityFrameworkCore;
using Waybill.Tests.Integration.G3;

namespace Waybill.Tests.Integration.G4;

// Two deliveries of one key to one handler at once, outside the required consumption (one consumer, one delivery at a
// time): the key's row is locked from the sequence check to the commit, so the second waits for the first without
// entering its handler, then compares with what the first wrote. Never the higher applied before the lower; the lower
// arriving second is refused. With an existing key the second waits on the row (tuple); with a new key, on the first's
// insert (transactionid). The interleaving is forced: the second is let go only once PostgreSQL shows it waiting.
[Collection(PostgresCollection.Name)]
public sealed class G4_Inbox_ConcorrentesDaMesmaChave_NuncaForaDeOrdem(PostgresFixture postgres)
{
    [Theory]
    [InlineData(4L, 5L, true, "Processed")]
    [InlineData(5L, 4L, true, "Regressed")]
    [InlineData(4L, 5L, false, "Processed")]
    [InlineData(5L, 4L, false, "Regressed")]
    public async Task G4_Inbox_SegundaEsperaAPrimeiraEComparaComOQueElaGravou(long first, long second, bool existingKey, string expected)
    {
        var ct = TestContext.Current.CancellationToken;
        var database = await TestDatabase.CreateAsync(postgres);
        await using var services = InboxOrderHarness.Services(database, out _);
        if (existingKey)
            await InboxOrderHarness.DeliverAsync(services, Guid.CreateVersion7(), 1);
        var (entered, release) = (new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously), new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
        var firstId = Guid.CreateVersion7();

        var holding = Task.Run(async () =>
        {
            await using var scope = services.CreateAsyncScope();
            return await scope.ServiceProvider.GetRequiredService<IInbox<AppDbContext>>().ProcessInOrderAsync(
                InboxOrderHarness.Handler, firstId, new MessageOrder(InboxOrderHarness.Key, first), async (db, token) =>
                {
                    await InboxHarness.ApplyEffect(db, token);
                    entered.TrySetResult();
                    await release.Task.WaitAsync(token);
                });
        }, ct);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), ct);
        var calls = new HandlerCalls();
        var waiting = InboxOrderHarness.DeliverAsync(services, Guid.CreateVersion7(), second, calls);
        await KeyLockHarness.WaitForAsync(database, atBarrier: 0, onLocks: 1);
        Assert.Equal(0, calls.Count);

        release.SetResult();

        Assert.Equal(OrderedInboxResult.Processed, await holding.WaitAsync(TimeSpan.FromSeconds(10), ct));
        Assert.Equal(Enum.Parse<OrderedInboxResult>(expected), await waiting.WaitAsync(TimeSpan.FromSeconds(10), ct));
        Assert.Equal(Math.Max(first, second), (await InboxOrderHarness.LastAppliedAsync(database))!.Value.Sequence);
    }
}
