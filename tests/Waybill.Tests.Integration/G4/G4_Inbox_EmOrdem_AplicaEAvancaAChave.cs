using Waybill.EntityFrameworkCore;
using Waybill.Tests.Integration.G3;

namespace Waybill.Tests.Integration.G4;

// G4 on the consumer (stage 10): messages of a key arriving in order are applied, and the handler's key moves to the
// last one, with its message id, in the same transaction as the effect.
[Collection(PostgresCollection.Name)]
public sealed class G4_Inbox_EmOrdem_AplicaEAvancaAChave(PostgresFixture postgres)
{
    [Fact]
    public async Task G4_Inbox_EmOrdem_TresProcessadasEAChaveNaUltima()
    {
        var database = await TestDatabase.CreateAsync(postgres);
        await using var services = InboxOrderHarness.Services(database, out _);
        var ids = new[] { Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7() };

        for (var i = 0; i < ids.Length; i++)
        {
            Assert.Equal(OrderedInboxResult.Processed, await InboxOrderHarness.DeliverAsync(services, ids[i], i + 1));
            Assert.Equal((i + 1L, ids[i]), await InboxOrderHarness.LastAppliedAsync(database));
        }

        Assert.Equal(3, await database.EffectsAsync());
        Assert.Equal(3, await database.InboxRowsAsync());
    }
}
