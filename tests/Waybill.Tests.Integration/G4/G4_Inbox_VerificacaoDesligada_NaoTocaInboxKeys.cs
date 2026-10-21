using Waybill.EntityFrameworkCore;
using Waybill.Tests.Integration.G3;

namespace Waybill.Tests.Integration.G4;

// Verification off is v0.1's inbox: ProcessAsync never reads nor writes inbox_keys. Proven with the table locked
// exclusively by another session, so any access would wait: ProcessAsync finishes, and ProcessInOrderAsync with an
// order, the positive control, waits on that lock until it is dropped.
[Collection(PostgresCollection.Name)]
public sealed class G4_Inbox_VerificacaoDesligada_NaoTocaInboxKeys(PostgresFixture postgres)
{
    [Fact]
    public async Task G4_Inbox_ProcessAsyncComInboxKeysTravada_TerminaSemEsperar()
    {
        var database = await TestDatabase.CreateAsync(postgres);
        await using var services = InboxOrderHarness.Services(database, out _);
        await using var locker = await InboxOrderHarness.LockInboxKeysAsync(database);

        var results = await Task.WhenAll(
                InboxHarness.DeliverAsync(services, InboxOrderHarness.Handler, Guid.CreateVersion7()),
                InboxHarness.DeliverAsync(services, InboxOrderHarness.Handler, Guid.CreateVersion7()))
            .WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        Assert.All(results, r => Assert.Equal(InboxResult.Processed, r));

        // The positive control: an ordered delivery does wait on the lock, so the one above would have too.
        var ordered = InboxOrderHarness.DeliverAsync(services, Guid.CreateVersion7(), 1);
        await KeyLockHarness.WaitForAsync(database, atBarrier: 0, onLocks: 1);
        Assert.False(ordered.IsCompleted);
        await locker.CloseAsync();
        Assert.Equal(OrderedInboxResult.Processed, await ordered.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
    }
}
