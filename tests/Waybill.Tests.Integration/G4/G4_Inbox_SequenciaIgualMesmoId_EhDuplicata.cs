using Microsoft.Extensions.Logging;
using Waybill.EntityFrameworkCore;
using Waybill.Tests.Integration.G3;

namespace Waybill.Tests.Integration.G4;

// The last applied message of a key, redelivered once inbox retention deleted its record: same sequence, same message id,
// so it is the same message, already applied. A duplicate, logged at Debug, never a regression. Delivering 1 and 2 first
// also checks that the key's message id moves with its sequence.
[Collection(PostgresCollection.Name)]
public sealed class G4_Inbox_SequenciaIgualMesmoId_EhDuplicata(PostgresFixture postgres)
{
    [Fact]
    public async Task G4_Inbox_UltimaAplicadaDeNovoSemRegistroNoInbox_EhDuplicata()
    {
        var database = await TestDatabase.CreateAsync(postgres);
        await using var services = InboxOrderHarness.Services(database, out var logs);
        var (first, second) = (Guid.CreateVersion7(), Guid.CreateVersion7());
        await InboxOrderHarness.DeliverAsync(services, first, 1);
        await InboxOrderHarness.DeliverAsync(services, second, 2);
        await InboxOrderHarness.ForgetInboxRecordsAsync(database);
        var calls = new HandlerCalls();

        Assert.Equal(OrderedInboxResult.Duplicate, await InboxOrderHarness.DeliverAsync(services, second, 2, calls));

        Assert.Equal(0, calls.Count);
        Assert.Equal(2, await database.EffectsAsync());
        Assert.Equal(0, await database.InboxRowsAsync());
        Assert.Empty(logs.InboxLogs(50));
        var duplicate = Assert.Single(logs.InboxLogs(51));
        Assert.Equal(LogLevel.Debug, duplicate.Level);
        Assert.Equal(second, duplicate.Fields["MessageId"]);
    }
}
