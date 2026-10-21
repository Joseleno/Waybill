using Waybill.EntityFrameworkCore;

namespace Waybill.Tests.Integration.G4;

// Sequences are tracked per handler: one handler's key at 5 says nothing about another handler of the same key.
[Collection(PostgresCollection.Name)]
public sealed class G4_Inbox_DoisHandlers_SequenciasIndependentes(PostgresFixture postgres)
{
    [Fact]
    public async Task G4_Inbox_DoisHandlers_UmEm5NaoBarraA3DoOutro()
    {
        var database = await TestDatabase.CreateAsync(postgres);
        await using var services = InboxOrderHarness.Services(database, out _);
        Assert.Equal(OrderedInboxResult.Processed, await InboxOrderHarness.DeliverAsync(services, Guid.CreateVersion7(), 5, handler: "receipts.issue"));

        Assert.Equal(OrderedInboxResult.Processed, await InboxOrderHarness.DeliverAsync(services, Guid.CreateVersion7(), 3, handler: "audit.record"));

        Assert.Equal(5, (await InboxOrderHarness.LastAppliedAsync(database, handler: "receipts.issue"))!.Value.Sequence);
        Assert.Equal(3, (await InboxOrderHarness.LastAppliedAsync(database, handler: "audit.record"))!.Value.Sequence);
    }
}
