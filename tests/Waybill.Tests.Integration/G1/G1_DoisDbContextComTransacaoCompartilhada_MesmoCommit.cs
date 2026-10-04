using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Waybill.EntityFrameworkCore;

namespace Waybill.Tests.Integration.G1;

// Several DbContexts are supported only with a shared connection and transaction: then the events of both commit
// or roll back together with all the data.
[Collection(PostgresCollection.Name)]
public sealed class G1_DoisDbContextComTransacaoCompartilhada_MesmoCommit(PostgresFixture postgres)
{
    [Theory]
    [InlineData(true, 2)]
    [InlineData(false, 0)]
    public async Task G1_DoisDbContextComTransacaoCompartilhada_MesmoCommit_OuMesmoRollback(bool commit, int expectedEvents)
    {
        var ct = TestContext.Current.CancellationToken;
        var database = await TestDatabase.CreateAsync(postgres);
        await using var services = database.Services(sharedConnection: _ => new NpgsqlConnection(database.ConnectionString));

        await using (var scope = services.CreateAsyncScope())
        {
            var billing = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var audit = scope.ServiceProvider.GetRequiredService<AuditDbContext>();

            await using var transaction = await billing.Database.BeginTransactionAsync(ct);
            await audit.Database.UseTransactionAsync(transaction.GetDbTransaction(), ct);

            var invoice = new Invoice { Number = "INV-1", Amount = 10m };
            billing.Invoices.Add(invoice);
            scope.ServiceProvider.GetRequiredService<IOutbox<AppDbContext>>().Enqueue(new InvoicePaid(invoice.Id, invoice.Amount));
            audit.Entries.Add(new AuditEntry { Action = "invoice-paid" });
            scope.ServiceProvider.GetRequiredService<IOutbox<AuditDbContext>>().Enqueue(new AuditRecorded("invoice-paid"));

            await billing.SaveChangesAsync(ct);
            await audit.SaveChangesAsync(ct);

            if (commit)
                await transaction.CommitAsync(ct);
            else
                await transaction.RollbackAsync(ct);
        }

        Assert.Equal(expectedEvents, await database.OutboxCountAsync());
        Assert.Equal(expectedEvents / 2, await database.ScalarAsync("SELECT count(*) FROM waybill.outbox WHERE type = 'audit.recorded.v1'"));
        Assert.Equal(expectedEvents / 2, await database.ScalarAsync("SELECT count(*) FROM invoices"));
        Assert.Equal(expectedEvents / 2, await database.ScalarAsync("SELECT count(*) FROM audit_entries"));
    }
}
