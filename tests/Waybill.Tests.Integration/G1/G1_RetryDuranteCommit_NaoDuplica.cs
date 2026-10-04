using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Waybill.EntityFrameworkCore;

namespace Waybill.Tests.Integration.G1;

// G1: the message id is fixed before the first commit, so re-executing a commit that had already succeeded fails
// on the duplicate key instead of producing a second event.
[Collection(PostgresCollection.Name)]
public sealed class G1_RetryDuranteCommit_NaoDuplica(PostgresFixture postgres)
{
    // With an invoice the duplicate may hit either primary key first; the event-only case pins it on the outbox.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task G1_RetryDuranteCommit_NaoDuplica_UmEventoESegundaTentativaFalhaPorChave(bool withData)
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

            // EF skips the transaction for a single-statement save; force it so the event-only case also commits.
            context.Database.AutoTransactionBehavior = AutoTransactionBehavior.Always;
            var invoice = new Invoice { Number = "INV-1", Amount = 10m };
            if (withData)
                context.Invoices.Add(invoice);
            outbox.Enqueue(new InvoicePaid(invoice.Id, invoice.Amount), key: invoice.Id.ToString());

            // The commit succeeds on the server but the client sees a transient connection error, so the
            // execution strategy runs SaveChanges again with the same tracked entities.
            error = await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync(ct));
        }

        Assert.True(lostCommitAck.Fired, "the simulated lost acknowledgement never happened");
        var duplicate = Assert.IsType<PostgresException>(error.InnerException);
        Assert.Equal(PostgresErrorCodes.UniqueViolation, duplicate.SqlState);
        if (!withData)
            Assert.Equal("pk_outbox", duplicate.ConstraintName);
        Assert.Equal(1, await database.OutboxCountAsync());
        Assert.Equal(withData ? 1 : 0, await database.ScalarAsync("SELECT count(*) FROM invoices"));
    }
}
