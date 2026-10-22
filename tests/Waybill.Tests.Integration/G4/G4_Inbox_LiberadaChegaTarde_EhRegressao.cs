using Waybill.EntityFrameworkCore;
using Waybill.Tests.Integration.G3;

namespace Waybill.Tests.Integration.G4;

// A message released from the outbox DLQ leaves a gap, and its key moves on. If an earlier attempt to publish it went
// unconfirmed, that copy may still arrive after the key's later messages (OPERATIONS.md, "A key stopped by the DLQ"):
// its message id is new to the inbox, its sequence is behind. A regression.
[Collection(PostgresCollection.Name)]
public sealed class G4_Inbox_LiberadaChegaTarde_EhRegressao(PostgresFixture postgres)
{
    [Fact]
    public async Task G4_Inbox_CopiaDaLiberadaDepoisDaSeguinte_EhRegressao()
    {
        var database = await TestDatabase.CreateAsync(postgres);
        await using var services = InboxOrderHarness.Services(database, out var logs);
        await InboxOrderHarness.DeliverAsync(services, Guid.CreateVersion7(), 1);
        await InboxOrderHarness.DeliverAsync(services, Guid.CreateVersion7(), 3); // 2 was released
        var calls = new HandlerCalls();

        Assert.Equal(OrderedInboxResult.Regressed, await InboxOrderHarness.DeliverAsync(services, Guid.CreateVersion7(), 2, calls));

        Assert.Equal(0, calls.Count);
        Assert.Equal(2, await database.EffectsAsync());
        var warning = Assert.Single(logs.InboxLogs(50));
        Assert.Equal("Lower", warning.Fields["Regression"]?.ToString());
        Assert.Equal((2L, 3L), (warning.Fields["Sequence"], warning.Fields["LastSequence"]));
    }
}
