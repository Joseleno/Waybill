using Waybill.EntityFrameworkCore;
using Waybill.Tests.Integration.G3;

namespace Waybill.Tests.Integration.G4;

// A producer restored from a backup numbers new messages with sequences its consumers already applied: a new message
// with the last applied sequence is not that message. A regression, logged as a reused sequence, never a duplicate
// that would be acknowledged and lost.
[Collection(PostgresCollection.Name)]
public sealed class G4_Inbox_SequenciaReusada_EhRegressao(PostgresFixture postgres)
{
    [Fact]
    public async Task G4_Inbox_OutraMensagemComAUltimaSequencia_EhRegressaoReusada()
    {
        var database = await TestDatabase.CreateAsync(postgres);
        await using var services = InboxOrderHarness.Services(database, out var logs);
        await InboxOrderHarness.DeliverAsync(services, Guid.CreateVersion7(), 1);
        var applied = Guid.CreateVersion7();
        await InboxOrderHarness.DeliverAsync(services, applied, 2);
        var reused = Guid.CreateVersion7();
        var calls = new HandlerCalls();

        Assert.Equal(OrderedInboxResult.Regressed, await InboxOrderHarness.DeliverAsync(services, reused, 2, calls));

        Assert.Equal(0, calls.Count);
        Assert.Equal(2, await database.EffectsAsync());
        Assert.Equal((2L, applied), await InboxOrderHarness.LastAppliedAsync(database));
        var warning = Assert.Single(logs.InboxLogs(50));
        Assert.Equal("Reused", warning.Fields["Regression"]?.ToString());
        Assert.Equal((reused, applied), (warning.Fields["MessageId"], warning.Fields["LastMessageId"]));
    }
}
