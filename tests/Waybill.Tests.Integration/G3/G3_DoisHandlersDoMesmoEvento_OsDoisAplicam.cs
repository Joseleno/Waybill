using Waybill.EntityFrameworkCore;

namespace Waybill.Tests.Integration.G3;

// The key is (handler, message_id): two handlers of the same event each apply once, and neither is mistaken for a
// duplicate of the other.
[Collection(PostgresCollection.Name)]
public sealed class G3_DoisHandlersDoMesmoEvento_OsDoisAplicam(PostgresFixture postgres)
{
    [Fact]
    public async Task G3_DoisHandlersDoMesmoEvento_OsDoisAplicam_CadaUmUmaVez()
    {
        var database = await TestDatabase.CreateAsync(postgres);
        await using var services = database.Services();
        var messageId = Guid.CreateVersion7();

        Assert.Equal(InboxResult.Processed, await InboxHarness.DeliverAsync(services, "billing.mark-paid", messageId));
        Assert.Equal(InboxResult.Processed, await InboxHarness.DeliverAsync(services, "notifications.send-receipt", messageId));
        Assert.Equal(InboxResult.Duplicate, await InboxHarness.DeliverAsync(services, "billing.mark-paid", messageId));
        Assert.Equal(InboxResult.Duplicate, await InboxHarness.DeliverAsync(services, "notifications.send-receipt", messageId));

        Assert.Equal(2, await database.EffectsAsync());
        Assert.Equal(2, await database.InboxRowsAsync());
    }
}
