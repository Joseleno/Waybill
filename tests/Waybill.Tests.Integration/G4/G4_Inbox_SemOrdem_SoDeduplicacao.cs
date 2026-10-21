using Waybill.EntityFrameworkCore;

namespace Waybill.Tests.Integration.G4;

// A message that is not ordered (no key, written or published with ordering off) carries no sequence: with
// ProcessInOrderAsync and no order it is only deduplicated, and inbox_keys is not touched, which the table locked
// exclusively by another session proves (any access would wait).
[Collection(PostgresCollection.Name)]
public sealed class G4_Inbox_SemOrdem_SoDeduplicacao(PostgresFixture postgres)
{
    [Fact]
    public async Task G4_Inbox_SemOrdem_ProcessaEDeduplicaSemTocarInboxKeys()
    {
        var ct = TestContext.Current.CancellationToken;
        var database = await TestDatabase.CreateAsync(postgres);
        await using var services = InboxOrderHarness.Services(database, out _);
        var messageId = Guid.CreateVersion7();
        await using (await InboxOrderHarness.LockInboxKeysAsync(database))
        {
            Assert.Equal(OrderedInboxResult.Processed,
                await InboxOrderHarness.DeliverAsync(services, messageId, order: null).WaitAsync(TimeSpan.FromSeconds(10), ct));
            Assert.Equal(OrderedInboxResult.Duplicate,
                await InboxOrderHarness.DeliverAsync(services, messageId, order: null).WaitAsync(TimeSpan.FromSeconds(10), ct));
        }

        Assert.Equal(0, await database.ScalarAsync("SELECT count(*) FROM waybill.inbox_keys"));
    }
}
