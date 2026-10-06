using Waybill.EntityFrameworkCore;

namespace Waybill.Tests.Integration.G3;

// G3: the effect written to the consumer's database is applied once, even with repeated deliveries.
[Collection(PostgresCollection.Name)]
public sealed class G3_EntregaDuplaEmSequencia_AplicaUmaVez(PostgresFixture postgres)
{
    [Fact]
    public async Task G3_EntregaDuplaEmSequencia_AplicaUmaVez_SegundaEhDuplicataSemChamarOHandler()
    {
        var database = await TestDatabase.CreateAsync(postgres);
        await using var services = database.Services();
        var messageId = Guid.CreateVersion7();
        var calls = 0;

        Task Handle(AppDbContext db, CancellationToken ct)
        {
            calls++;
            return InboxHarness.ApplyEffect(db, ct);
        }

        Assert.Equal(InboxResult.Processed, await InboxHarness.DeliverAsync(services, "billing.mark-paid", messageId, Handle));
        Assert.Equal(InboxResult.Duplicate, await InboxHarness.DeliverAsync(services, "billing.mark-paid", messageId, Handle));
        Assert.Equal(InboxResult.Duplicate, await InboxHarness.DeliverAsync(services, "billing.mark-paid", messageId, Handle));

        Assert.Equal(1, calls);
        Assert.Equal(1, await database.EffectsAsync());
        Assert.Equal(1, await database.InboxRowsAsync());
    }
}
