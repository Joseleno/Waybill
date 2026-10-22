using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Waybill.EntityFrameworkCore;

namespace Waybill.Tests.Integration.G4;

// A limit, not a guarantee (OPERATIONS.md, ordering by key). A key's counter stays locked until commit, so a transaction
// that enqueues K and then writes the application row R deadlocks with one that writes R and then enqueues K. No key
// list can help: the lock on K is taken before R is known. The database aborts one of them (40P01); enqueuing in the
// last SaveChanges before commit avoids it.
[Collection(PostgresCollection.Name)]
public sealed class Limite_EscritaDepoisDeEnfileirar_PodeDarDeadlock(PostgresFixture postgres)
{
    [Fact]
    public async Task Limite_EscritaDepoisDeEnfileirar_UmaTransacaoAbortadaComDeadlock()
    {
        var database = await TestDatabase.CreateAsync(postgres);
        await KeyLockHarness.EnableOrderingAsync(database);
        var invoice = await KeyLockHarness.SeedInvoiceAsync(database);
        await using var barrier = await Barrier.InstallAsync(database, "waybill.outbox", "INSERT");

        async Task<PostgresException?> Run(string applicationName, Func<AppDbContext, IOutbox<AppDbContext>, Task> work)
        {
            await using var services = database.Services(sharedConnection: _ => new NpgsqlConnection(KeyLockHarness.ConnectionString(database, applicationName)));
            await using var scope = services.CreateAsyncScope();
            var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            try
            {
                await work(context, scope.ServiceProvider.GetRequiredService<IOutbox<AppDbContext>>());
                return null;
            }
            catch (Exception error) when (KeyLockHarness.PostgresError(error) is { } postgres)
            {
                return postgres;
            }
        }

        var enqueueThenWrite = Task.Run(() => Run(KeyLockHarness.BarrierApp, async (context, outbox) =>
        {
            await using var transaction = await context.Database.BeginTransactionAsync();
            outbox.Enqueue(new InvoicePaid(invoice, 1m), "K");
            await context.SaveChangesAsync();                 // holds K; stops at the barrier
            (await context.Invoices.SingleAsync(i => i.Id == invoice)).Amount += 1;
            await context.SaveChangesAsync();                 // then wants the invoice
            await transaction.CommitAsync();
        }));
        await barrier.WaitForAsync(atBarrier: 1, onLocks: 0);
        var writeThenEnqueue = Task.Run(() => Run("app", async (context, outbox) =>
        {
            (await context.Invoices.SingleAsync(i => i.Id == invoice)).Amount += 1;
            outbox.Enqueue(new InvoicePaid(invoice, 2m), "K");
            await context.SaveChangesAsync();                 // holds the invoice, waits for K
        }));
        await barrier.WaitForAsync(atBarrier: 1, onLocks: 1);
        await barrier.ReleaseAsync();
        var errors = await Task.WhenAll(enqueueThenWrite, writeThenEnqueue);

        Assert.Equal([PostgresErrorCodes.DeadlockDetected], errors.Where(e => e is not null).Select(e => e!.SqlState));
    }
}
