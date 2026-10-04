using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Waybill.EntityFrameworkCore;

namespace Waybill.Tests.Integration.G1;

// G1: the message id is fixed before the first commit, so re-executing a commit that had already succeeded fails
// on the duplicate key instead of producing a second event.
[Collection(PostgresCollection.Name)]
public sealed class G1_RetryDuranteCommit_NaoDuplica(PostgresFixture postgres)
{
    [Fact]
    public async Task G1_RetryDuranteCommit_NaoDuplica_UmEventoESegundaTentativaFalhaPorChave()
    {
        var ct = TestContext.Current.CancellationToken;
        var database = await TestDatabase.CreateAsync(postgres);
        var lostCommitAck = new LoseFirstCommitAcknowledgement();
        await using var services = database.Services(
            npgsql: o => o.EnableRetryOnFailure(3, TimeSpan.FromMilliseconds(10), null),
            interceptors: lostCommitAck);

        DbUpdateException error;
        await using (var scope = services.CreateAsyncScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var outbox = scope.ServiceProvider.GetRequiredService<IOutbox<AppDbContext>>();

            var invoice = new Invoice { Number = "INV-1", Amount = 10m };
            context.Invoices.Add(invoice);
            outbox.Enqueue(new InvoicePaid(invoice.Id, invoice.Amount), key: invoice.Id.ToString());

            // The commit succeeds on the server but the client sees a transient connection error, so the
            // execution strategy runs SaveChanges again with the same tracked entities.
            error = await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync(ct));
        }

        Assert.True(lostCommitAck.Fired, "the simulated lost acknowledgement never happened");
        Assert.Equal(PostgresErrorCodes.UniqueViolation, Assert.IsType<PostgresException>(error.InnerException).SqlState);
        Assert.Equal(1, await database.OutboxCountAsync());
        Assert.Equal(1, await database.ScalarAsync("SELECT count(*) FROM invoices"));
    }

    // The COMMIT reaches the server; the acknowledgement is "lost" on the way back, once.
    private sealed class LoseFirstCommitAcknowledgement : DbTransactionInterceptor
    {
        public bool Fired { get; private set; }

        public override void TransactionCommitted(DbTransaction transaction, TransactionEndEventData eventData) => LoseOnce();

        public override Task TransactionCommittedAsync(
            DbTransaction transaction, TransactionEndEventData eventData, CancellationToken cancellationToken = default)
        {
            LoseOnce();
            return Task.CompletedTask;
        }

        private void LoseOnce()
        {
            if (Fired)
                return;
            Fired = true;
            throw new NpgsqlException("Simulated connection loss after COMMIT.", new IOException("connection reset"));
        }
    }
}
