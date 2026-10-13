using Microsoft.Extensions.DependencyInjection;
using Waybill.EntityFrameworkCore;

namespace Waybill.Tests.Integration.G4;

// Messages of one key enqueued in one transaction commit together; their order is the order they were enqueued in
// (OrderCreated, then OrderPaid). The trigger numbers rows in the order EF inserts them, which is id order, so ids must
// follow enqueue order even within one millisecond: a plain UUIDv7 does not (ADR 0008).
[Collection(PostgresCollection.Name)]
public sealed class G4_MesmaChaveNaMesmaTransacao_SequenciaNaOrdemDoEnqueue(PostgresFixture postgres)
{
    [Fact]
    public async Task G4_MesmaChaveNaMesmaTransacao_SequenciaNaOrdemDoEnqueue_Rajada()
    {
        var database = await TestDatabase.CreateAsync(postgres);
        await KeyLockHarness.EnableOrderingAsync(database);
        await using var services = database.Services();
        await using var scope = services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var outbox = scope.ServiceProvider.GetRequiredService<IOutbox<AppDbContext>>();

        // A burst, well inside a few milliseconds: amounts 1..200 in enqueue order, one key.
        for (var i = 1; i <= 200; i++)
            outbox.Enqueue(new InvoicePaid(Guid.NewGuid(), i), "order-1");
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        Assert.Equal(0, await database.ScalarAsync("""
            SELECT count(*) FROM waybill.outbox
            WHERE key = 'order-1' AND sequence <> (convert_from(payload, 'UTF8')::jsonb ->> 'Amount')::int
            """));
    }
}
