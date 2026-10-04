using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Waybill.EntityFrameworkCore;

namespace Waybill.Tests.Integration.G1;

// G1: a SaveChanges that fails and is repeated in the same transaction materializes each event once.
[Collection(PostgresCollection.Name)]
public sealed class G1_SaveChangesRepetidoNaMesmaTransacao_UmaLinhaPorEvento(PostgresFixture postgres)
{
    [Fact]
    public async Task G1_SaveChangesRepetidoNaMesmaTransacao_UmaLinhaPorEvento_AposFalhaECorrecao()
    {
        var ct = TestContext.Current.CancellationToken;
        var database = await TestDatabase.CreateAsync(postgres);
        await using var services = database.Services();

        await using (var seed = services.CreateAsyncScope())
        {
            var context = seed.ServiceProvider.GetRequiredService<AppDbContext>();
            context.Invoices.Add(new Invoice { Number = "INV-1", Amount = 1m });
            await context.SaveChangesAsync(ct);
        }

        await using (var scope = services.CreateAsyncScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var outbox = scope.ServiceProvider.GetRequiredService<IOutbox<AppDbContext>>();
            await using var transaction = await context.Database.BeginTransactionAsync(ct);

            var invoice = new Invoice { Number = "INV-1", Amount = 10m }; // violates the unique number
            context.Invoices.Add(invoice);
            outbox.Enqueue(new InvoicePaid(invoice.Id, invoice.Amount), key: invoice.Id.ToString());
            outbox.Enqueue(new InvoicePaid(invoice.Id, invoice.Amount), key: invoice.Id.ToString());

            // EF rolls the transaction back to its savepoint; the outbox records stay pending and tracked.
            await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync(ct));

            invoice.Number = "INV-2";
            await context.SaveChangesAsync(ct);
            await context.SaveChangesAsync(ct); // a further save in the same transaction adds nothing
            await transaction.CommitAsync(ct);
        }

        Assert.Equal(2, await database.OutboxCountAsync());
        Assert.Equal(2, await database.ScalarAsync("SELECT count(DISTINCT id) FROM waybill.outbox"));
        Assert.Equal(2, await database.ScalarAsync("SELECT count(*) FROM invoices"));
    }

    // EF's recommended pattern with a retrying strategy: SaveChanges(acceptAllChangesOnSuccess: false) inside
    // the retried block, AcceptAllChanges after it. The first attempt saves and then rolls back; the retry must
    // insert the event again together with the invoice. Detaching the outbox records after the first save would
    // lose the event here.
    [Fact]
    public async Task G1_SaveChangesSemAceitarRepetidoPelaEstrategia_ReinsereOEvento()
    {
        var ct = TestContext.Current.CancellationToken;
        var database = await TestDatabase.CreateAsync(postgres);
        await using var services = database.Services(npgsql: o => o.EnableRetryOnFailure(3, TimeSpan.FromMilliseconds(10), null));

        var attempts = 0;
        await using (var scope = services.CreateAsyncScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var outbox = scope.ServiceProvider.GetRequiredService<IOutbox<AppDbContext>>();
            var invoice = new Invoice { Number = "INV-1", Amount = 10m };
            context.Invoices.Add(invoice);
            outbox.Enqueue(new InvoicePaid(invoice.Id, invoice.Amount), key: invoice.Id.ToString());

            await context.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
            {
                await using var transaction = await context.Database.BeginTransactionAsync(ct);
                await context.SaveChangesAsync(acceptAllChangesOnSuccess: false, ct);
                if (++attempts == 1)
                    throw new Npgsql.NpgsqlException("Simulated transient failure before COMMIT.", new IOException("connection reset"));
                await transaction.CommitAsync(ct);
            });
            context.ChangeTracker.AcceptAllChanges();
        }

        Assert.Equal(2, attempts);
        Assert.Equal(1, await database.ScalarAsync("SELECT count(*) FROM invoices"));
        Assert.Equal(1, await database.OutboxCountAsync());
    }
}
