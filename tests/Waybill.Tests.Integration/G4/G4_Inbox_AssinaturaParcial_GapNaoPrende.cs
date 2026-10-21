using Waybill.EntityFrameworkCore;

namespace Waybill.Tests.Integration.G4;

// A consumer that subscribes to some of a key's message types sees gaps in its sequence, and so does one after a
// release: a gap is not an error, and the inbox never waits for the missing messages (it detects; it does not reorder).
[Collection(PostgresCollection.Name)]
public sealed class G4_Inbox_AssinaturaParcial_GapNaoPrende(PostgresFixture postgres)
{
    [Fact]
    public async Task G4_Inbox_AssinaturaParcial_SequenciasComGapSaoProcessadas()
    {
        var database = await TestDatabase.CreateAsync(postgres);
        await using var services = InboxOrderHarness.Services(database, out _);

        foreach (var sequence in new long[] { 2, 5, 9 })
            Assert.Equal(OrderedInboxResult.Processed, await InboxOrderHarness.DeliverAsync(services, Guid.CreateVersion7(), sequence));

        Assert.Equal(9, (await InboxOrderHarness.LastAppliedAsync(database))!.Value.Sequence);
    }
}
