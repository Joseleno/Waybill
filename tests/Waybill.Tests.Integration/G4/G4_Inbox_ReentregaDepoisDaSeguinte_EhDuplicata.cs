using Waybill.EntityFrameworkCore;
using Waybill.Tests.Integration.G3;

namespace Waybill.Tests.Integration.G4;

// Deduplication comes before the sequence: a redelivery of an applied message, arriving after the next one, is a
// duplicate. The other way round it would be a regression, and a legitimate redelivery would go to the DLX.
[Collection(PostgresCollection.Name)]
public sealed class G4_Inbox_ReentregaDepoisDaSeguinte_EhDuplicata(PostgresFixture postgres)
{
    [Fact]
    public async Task G4_Inbox_ReentregaDaPrimeiraDepoisDaSegunda_EhDuplicata()
    {
        var database = await TestDatabase.CreateAsync(postgres);
        await using var services = InboxOrderHarness.Services(database, out var logs);
        var (first, second) = (Guid.CreateVersion7(), Guid.CreateVersion7());
        await InboxOrderHarness.DeliverAsync(services, first, 1);
        await InboxOrderHarness.DeliverAsync(services, second, 2);
        var calls = new HandlerCalls();

        Assert.Equal(OrderedInboxResult.Duplicate, await InboxOrderHarness.DeliverAsync(services, first, 1, calls));

        Assert.Equal(0, calls.Count);
        Assert.Equal(2, await database.EffectsAsync());
        Assert.Equal((2L, second), await InboxOrderHarness.LastAppliedAsync(database));
        Assert.Empty(logs.InboxLogs(50));
    }
}
