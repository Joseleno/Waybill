using Waybill.EntityFrameworkCore;

namespace Waybill.Tests.Integration.G3;

// The ack is the application's and comes after the commit. If it is lost (connection drop right after the commit),
// the broker redelivers; the redelivery is a duplicate and only needs the ack.
[Collection(PostgresCollection.Name)]
public sealed class G3_FalhaDoAckDepoisDoCommit_ReentregaEhDuplicata(PostgresFixture postgres)
{
    [Fact]
    public async Task G3_FalhaDoAckDepoisDoCommit_ReentregaEhDuplicataESoFazAck()
    {
        var database = await TestDatabase.CreateAsync(postgres);
        await using var services = database.Services();
        var messageId = Guid.CreateVersion7();
        var acks = 0;

        async Task ConsumeAsync(bool ackFails)
        {
            await InboxHarness.DeliverAsync(services, "billing.mark-paid", messageId);
            if (ackFails)
                return; // the channel dropped before basic.ack reached the broker: it will redeliver
            acks++;
        }

        await ConsumeAsync(ackFails: true);
        Assert.Equal(1, await database.EffectsAsync());

        Assert.Equal(InboxResult.Duplicate, await InboxHarness.DeliverAsync(services, "billing.mark-paid", messageId));
        await ConsumeAsync(ackFails: false);

        Assert.Equal(1, acks);
        Assert.Equal(1, await database.EffectsAsync());
    }
}
