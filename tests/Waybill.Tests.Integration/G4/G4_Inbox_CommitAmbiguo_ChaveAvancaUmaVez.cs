using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;
using Waybill.EntityFrameworkCore;
using Waybill.Tests.Integration.G3;

namespace Waybill.Tests.Integration.G4;

// The commit reaches the server and its acknowledgement is lost: the execution strategy runs the unit again. The retry
// finds its own inbox record and returns Duplicate: the effect applies once, the key moved once. In the second test the
// key's next message is applied between the lost acknowledgement and the retry. Deduplication comes first, so the retry
// is still a duplicate; checked the other way round, it would be a regression of an applied message.
[Collection(PostgresCollection.Name)]
public sealed class G4_Inbox_CommitAmbiguo_ChaveAvancaUmaVez(PostgresFixture postgres)
{
    [Fact]
    public async Task G4_Inbox_CommitAmbiguo_DuplicataEChaveNaMensagem()
    {
        var database = await TestDatabase.CreateAsync(postgres);
        var lostCommitAck = new LoseFirstCommitAcknowledgement();
        await using var services = InboxOrderHarness.Services(database, out _, o => o.EnableRetryOnFailure(3, TimeSpan.FromMilliseconds(10), null), lostCommitAck);
        var (messageId, calls) = (Guid.CreateVersion7(), new HandlerCalls());

        var result = await InboxOrderHarness.DeliverAsync(services, messageId, 1, calls);

        Assert.True(lostCommitAck.Fired, "the simulated lost acknowledgement never happened");
        Assert.Equal(OrderedInboxResult.Duplicate, result);
        Assert.Equal(1, calls.Count);
        Assert.Equal(1, await database.EffectsAsync());
        Assert.Equal((1L, messageId), await InboxOrderHarness.LastAppliedAsync(database));
    }

    [Fact]
    public async Task G4_Inbox_CommitAmbiguoComASeguinteNoMeio_EhDuplicata()
    {
        var database = await TestDatabase.CreateAsync(postgres);
        var next = Guid.CreateVersion7();
        IServiceProvider? container = null;
        var lostCommitAck = new LoseCommitAcknowledgementAfter(() => InboxOrderHarness.DeliverAsync(container!, next, 2));
        await using var services = InboxOrderHarness.Services(database, out _, o => o.EnableRetryOnFailure(3, TimeSpan.FromMilliseconds(10), null), lostCommitAck);
        container = services;
        var messageId = Guid.CreateVersion7();

        var result = await InboxOrderHarness.DeliverAsync(services, messageId, 1);

        Assert.Equal(OrderedInboxResult.Processed, await lostCommitAck.Meanwhile!);
        Assert.Equal(OrderedInboxResult.Duplicate, result);
        Assert.Equal(2, await database.EffectsAsync());
        Assert.Equal((2L, next), await InboxOrderHarness.LastAppliedAsync(database));
    }

    // Like LoseFirstCommitAcknowledgement, and before the error reaches the strategy, processes another message.
    private sealed class LoseCommitAcknowledgementAfter(Func<Task<OrderedInboxResult>> meanwhile) : DbTransactionInterceptor
    {
        public Task<OrderedInboxResult>? Meanwhile { get; private set; }

        public override async Task TransactionCommittedAsync(
            DbTransaction transaction, TransactionEndEventData eventData, CancellationToken cancellationToken = default)
        {
            if (Meanwhile is not null)
                return; // the delivery made meanwhile commits through this interceptor too
            Meanwhile = meanwhile();
            await Meanwhile;
            throw new NpgsqlException("Simulated connection loss after COMMIT.", new IOException("connection reset"));
        }
    }
}
