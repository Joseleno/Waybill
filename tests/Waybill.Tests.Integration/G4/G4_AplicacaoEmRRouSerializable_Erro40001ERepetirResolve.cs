using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Waybill.EntityFrameworkCore;

namespace Waybill.Tests.Integration.G4;

// A limit (OPERATIONS.md): the counter is a row every transaction of a key updates. In REPEATABLE READ or SERIALIZABLE,
// enqueuing on a key another transaction incremented after this one's snapshot fails with 40001, and retrying succeeds
// with the next sequence. In READ COMMITTED, the default, it simply waits.
[Collection(PostgresCollection.Name)]
public sealed class G4_AplicacaoEmRRouSerializable_Erro40001ERepetirResolve(PostgresFixture postgres)
{
    [Theory]
    [InlineData(IsolationLevel.RepeatableRead)]
    [InlineData(IsolationLevel.Serializable)]
    public async Task G4_AplicacaoEmRRouSerializable_ChaveIncrementadaDepoisDoSnapshot(IsolationLevel isolation)
    {
        var database = await TestDatabase.CreateAsync(postgres);
        await KeyLockHarness.EnableOrderingAsync(database);
        await KeyLockHarness.EnqueueAsync(database, "app", "K"); // K exists: sequence 1
        await using var services = database.Services();
        await using var scope = services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var outbox = scope.ServiceProvider.GetRequiredService<IOutbox<AppDbContext>>();
        var ct = TestContext.Current.CancellationToken;

        await using (var transaction = await context.Database.BeginTransactionAsync(isolation, ct))
        {
            await context.Invoices.CountAsync(ct);                   // the snapshot is taken here
            await KeyLockHarness.EnqueueAsync(database, "app", "K"); // another transaction takes sequence 2
            outbox.Enqueue(new InvoicePaid(Guid.NewGuid(), 1m), "K");
            // EF wraps it in an InvalidOperationException naming a transient failure: Npgsql counts 40001 as transient.
            var error = await Assert.ThrowsAnyAsync<Exception>(() => context.SaveChangesAsync(ct));
            Assert.Equal(PostgresErrorCodes.SerializationFailure, KeyLockHarness.PostgresError(error)?.SqlState);
            await transaction.RollbackAsync(ct);
        }

        await using (var retry = await context.Database.BeginTransactionAsync(isolation, ct))
        {
            await context.SaveChangesAsync(ct); // the message is still Added
            await retry.CommitAsync(ct);
        }

        Assert.Equal(["K:3:1:3:3"], await KeyLockHarness.SequencesAsync(database));
    }
}
