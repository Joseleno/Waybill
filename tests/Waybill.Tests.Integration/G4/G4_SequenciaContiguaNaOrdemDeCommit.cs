using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Waybill.EntityFrameworkCore;

namespace Waybill.Tests.Integration.G4;

// A key's sequences follow commit order with no gap and no repeat (ADR 0008): the counter row is locked from the INSERT
// to the commit, and a rolled-back increment goes with its transaction, savepoints included.
[Collection(PostgresCollection.Name)]
public sealed class G4_SequenciaContiguaNaOrdemDeCommit(PostgresFixture postgres)
{
    // A holds sequence 1 in an open transaction; B waits for the key; A rolls back, and B gets 1, not 2.
    [Fact]
    public async Task G4_SequenciaContigua_RollbackForcado_SeguinteFicaComONumero()
    {
        var database = await TestDatabase.CreateAsync(postgres);
        await KeyLockHarness.EnableOrderingAsync(database);
        await using var first = new NpgsqlConnection(database.ConnectionString);
        await first.OpenAsync(TestContext.Current.CancellationToken);
        await using var transaction = await first.BeginTransactionAsync(TestContext.Current.CancellationToken);
        await KeyLockHarness.InsertAsync(first, "K");

        var second = KeyLockHarness.EnqueueAsync(database, "app", "K");
        await KeyLockHarness.WaitForAsync(database, atBarrier: 0, onLocks: 1);
        await transaction.RollbackAsync(TestContext.Current.CancellationToken);
        await second;

        Assert.Equal(["K:1:1:1:1"], await KeyLockHarness.SequencesAsync(database));
    }

    // EF saves inside the application's transaction behind a savepoint and rolls back to it when the save fails: the
    // increments already made by that save, and the counter rows it created, go back too.
    [Fact]
    public async Task G4_SequenciaContigua_FalhaDepoisDoTriggerNumSavepoint_NaoGastaNumero()
    {
        var database = await TestDatabase.CreateAsync(postgres);
        await KeyLockHarness.EnableOrderingAsync(database);
        await database.ExecuteAsync("""
            CREATE FUNCTION public.test_refuse() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN RAISE EXCEPTION 'refused by the test'; END $$;
            CREATE TRIGGER test_refuse AFTER INSERT ON waybill.outbox FOR EACH ROW WHEN (NEW.key = 'K-fail')
                EXECUTE FUNCTION public.test_refuse();
            """);
        await using var services = database.Services();
        await using var scope = services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var outbox = scope.ServiceProvider.GetRequiredService<IOutbox<AppDbContext>>();
        var ct = TestContext.Current.CancellationToken;

        await using var transaction = await context.Database.BeginTransactionAsync(ct);
        outbox.Enqueue(new InvoicePaid(Guid.NewGuid(), 1m), "K");
        outbox.Enqueue(new InvoicePaid(Guid.NewGuid(), 2m), "K-fail"); // numbered by the trigger, then refused
        await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync(ct));
        foreach (var refused in context.ChangeTracker.Entries<OutboxRecord>().Where(e => e.Entity.Key == "K-fail").ToList())
            refused.State = EntityState.Detached;
        await context.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);

        Assert.Equal(["K:1:1:1:1"], await KeyLockHarness.SequencesAsync(database));
    }

    // Sampling, not proof: concurrent writers on one key, some rolling back, while a reader checks in one statement that
    // what it sees is always a contiguous prefix (no sequence visible before an earlier one).
    [Fact]
    public async Task G4_SequenciaContigua_LeitorConcorrenteSempreVePrefixoContiguo()
    {
        var database = await TestDatabase.CreateAsync(postgres);
        await KeyLockHarness.EnableOrderingAsync(database);
        await using var services = database.Services();
        var stop = DateTime.UtcNow.AddSeconds(3);
        var committed = 0;

        var writers = Enumerable.Range(0, 8).Select(w => Task.Run(async () =>
        {
            var random = new Random(w);
            while (DateTime.UtcNow < stop)
            {
                await using var scope = services.CreateAsyncScope();
                var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                await using var transaction = await context.Database.BeginTransactionAsync();
                scope.ServiceProvider.GetRequiredService<IOutbox<AppDbContext>>().Enqueue(new InvoicePaid(Guid.NewGuid(), 1m), "K");
                await context.SaveChangesAsync();
                if (random.Next(10) < 3)
                {
                    await transaction.RollbackAsync();
                }
                else
                {
                    await transaction.CommitAsync();
                    Interlocked.Increment(ref committed);
                }
            }
        })).ToArray();

        var samples = 0;
        await using var reader = new NpgsqlConnection(database.ConnectionString);
        await reader.OpenAsync(TestContext.Current.CancellationToken);
        while (!writers.All(t => t.IsCompleted))
        {
            await using var command = new NpgsqlCommand("SELECT count(*) = coalesce(max(sequence), 0) FROM waybill.outbox WHERE key = 'K'", reader);
            Assert.True((bool)(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken))!, $"sample {samples} saw a gap");
            samples++;
        }
        await Task.WhenAll(writers);

        Assert.True(samples > 10, $"only {samples} samples");
        Assert.Equal([$"K:{committed}:1:{committed}:{committed}"], await KeyLockHarness.SequencesAsync(database));
    }
}
