using Microsoft.Extensions.Logging;
using Waybill.EntityFrameworkCore;
using Waybill.Tests.Integration.G3;

namespace Waybill.Tests.Integration.G4;

// G4 on the consumer (stage 10): a late copy of an applied message, once inbox retention deleted its record, passes
// deduplication; its sequence is behind the key's, so it is refused. Nothing runs, nothing is recorded (a redelivery is
// refused again, never a duplicate that would be acknowledged), and the warning says why.
[Collection(PostgresCollection.Name)]
public sealed class G4_Inbox_RegressaoDepoisDaRetencao_NaoAplica(PostgresFixture postgres)
{
    [Fact]
    public async Task G4_Inbox_CopiaTardiaDepoisDaRetencao_RegressaoSemEfeitoNemRegistro()
    {
        var database = await TestDatabase.CreateAsync(postgres);
        await using var services = InboxOrderHarness.Services(database, out var logs);
        var (first, second) = (Guid.CreateVersion7(), Guid.CreateVersion7());
        await InboxOrderHarness.DeliverAsync(services, first, 1);
        await InboxOrderHarness.DeliverAsync(services, second, 2);
        await InboxOrderHarness.ForgetInboxRecordsAsync(database);
        var calls = new HandlerCalls();

        Assert.Equal(OrderedInboxResult.Regressed, await InboxOrderHarness.DeliverAsync(services, first, 1, calls));
        Assert.Equal(OrderedInboxResult.Regressed, await InboxOrderHarness.DeliverAsync(services, first, 1, calls));

        Assert.Equal(0, calls.Count);
        Assert.Equal(2, await database.EffectsAsync());
        Assert.Equal(0, await database.InboxRowsAsync());
        Assert.Equal((2L, second), await InboxOrderHarness.LastAppliedAsync(database));
        var warnings = logs.InboxLogs(50);
        Assert.Equal(2, warnings.Length);
        Assert.Equal(LogLevel.Warning, warnings[0].Level);
        Assert.Equal(InboxOrderHarness.Handler, warnings[0].Fields["Handler"]);
        Assert.Equal(InboxOrderHarness.Key, warnings[0].Fields["Key"]);
        Assert.Equal(1L, warnings[0].Fields["Sequence"]);
        Assert.Equal(2L, warnings[0].Fields["LastSequence"]);
        Assert.Equal(first, warnings[0].Fields["MessageId"]);
        Assert.Equal(second, warnings[0].Fields["LastMessageId"]);
        Assert.Equal("Lower", warnings[0].Fields["Regression"]?.ToString());
    }

    [Fact]
    public async Task G4_Inbox_OrdemDefault_Recusada()
    {
        var database = await TestDatabase.CreateAsync(postgres);
        await using var services = InboxOrderHarness.Services(database, out _);

        var error = await Assert.ThrowsAsync<ArgumentException>(() => InboxOrderHarness.DeliverAsync(services, Guid.CreateVersion7(), default(MessageOrder)));

        Assert.Equal("order", error.ParamName);
        Assert.Equal(0, await database.InboxRowsAsync());
    }
}
