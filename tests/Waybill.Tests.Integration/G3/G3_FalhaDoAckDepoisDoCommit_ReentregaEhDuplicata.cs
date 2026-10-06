using Waybill.EntityFrameworkCore;

namespace Waybill.Tests.Integration.G3;

// The ack is the application's and comes after the commit. If it is lost (connection drop right after the commit),
// the broker redelivers; the redelivery is a duplicate and only needs the ack. The broker side of this (ack after
// ProcessAsync, redelivery with the same message_id) is G3_ConsumidorRabbitMqSemFramework.
[Collection(PostgresCollection.Name)]
public sealed class G3_FalhaDoAckDepoisDoCommit_ReentregaEhDuplicata(PostgresFixture postgres)
{
    [Fact]
    public async Task G3_FalhaDoAckDepoisDoCommit_ReentregaEhDuplicataESoFazAck()
    {
        var database = await TestDatabase.CreateAsync(postgres);
        await using var services = database.Services();
        var messageId = Guid.CreateVersion7();
        var acked = new List<InboxResult>();

        async Task<InboxResult> ConsumeAsync(bool ackLost)
        {
            var result = await InboxHarness.DeliverAsync(services, "billing.mark-paid", messageId);
            if (!ackLost)
                acked.Add(result); // basic.ack, after the commit; a lost one makes the broker redeliver
            return result;
        }

        Assert.Equal(InboxResult.Processed, await ConsumeAsync(ackLost: true));
        Assert.Equal(InboxResult.Duplicate, await ConsumeAsync(ackLost: false)); // the redelivery

        Assert.Equal([InboxResult.Duplicate], acked);
        Assert.Equal(1, await database.EffectsAsync());
        Assert.Equal(1, await database.InboxRowsAsync());
    }
}
