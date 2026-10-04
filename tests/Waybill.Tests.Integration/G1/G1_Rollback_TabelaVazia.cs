using Microsoft.Extensions.DependencyInjection;
using Waybill.EntityFrameworkCore;

namespace Waybill.Tests.Integration.G1;

// G1: the event exists if, and only if, the transaction commits.
[Collection(PostgresCollection.Name)]
public sealed class G1_Rollback_TabelaVazia(PostgresFixture postgres)
{
    [Theory]
    [InlineData(false, 0)]
    [InlineData(true, 1)]
    public async Task G1_Rollback_TabelaVazia_ECommit_UmEvento(bool commit, int expectedEvents)
    {
        var ct = TestContext.Current.CancellationToken;
        var database = await TestDatabase.CreateAsync(postgres);
        await using var services = database.Services();

        await using (var scope = services.CreateAsyncScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var outbox = scope.ServiceProvider.GetRequiredService<IOutbox<AppDbContext>>();

            await using var transaction = await context.Database.BeginTransactionAsync(ct);
            var invoice = new Invoice { Number = "INV-1", Amount = 10m };
            context.Invoices.Add(invoice);
            outbox.Enqueue(new InvoicePaid(invoice.Id, invoice.Amount), key: invoice.Id.ToString());
            await context.SaveChangesAsync(ct);

            if (commit)
                await transaction.CommitAsync(ct);
            else
                await transaction.RollbackAsync(ct);
        }

        Assert.Equal(expectedEvents, await database.OutboxCountAsync());
        Assert.Equal(expectedEvents, await database.ScalarAsync("SELECT count(*) FROM invoices"));
        if (commit)
        {
            Assert.Equal(1, await database.ScalarAsync(
                "SELECT count(*) FROM waybill.outbox WHERE type = 'billing.invoice-paid.v1' AND status = 'pending' AND key IS NOT NULL"));
        }
    }
}
