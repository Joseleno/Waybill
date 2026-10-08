using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;
using Waybill.EntityFrameworkCore;

namespace Waybill.Tests.Integration.G4;

// The key list rides on the rows of one SaveChanges, not on the session (ADR 0008): a failed save, a pooled context, a
// transaction shared by two contexts or a retry of the execution strategy leaves nothing behind. Oracle: the counters
// are exactly the keys saved, and no stored row keeps its list. A stale list would lock, and create, keys never saved.
[Collection(PostgresCollection.Name)]
public sealed class G4_ListaDeChaves_NaoSobreviveAoSaveChanges(PostgresFixture postgres)
{
    [Fact]
    public async Task G4_ListaDeChaves_SaveChangesQueFalhaNumSavepoint_ListaNaoVazaParaOSeguinte()
    {
        var database = await StartAsync();
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
        outbox.Enqueue(new InvoicePaid(Guid.NewGuid(), 1m), "A");
        outbox.Enqueue(new InvoicePaid(Guid.NewGuid(), 1m), "B");
        outbox.Enqueue(new InvoicePaid(Guid.NewGuid(), 1m), "K-fail");
        await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync(ct));
        context.ChangeTracker.Clear();
        outbox.Enqueue(new InvoicePaid(Guid.NewGuid(), 1m), "C");
        await context.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);

        await AssertSavedKeysAsync(database, "C:1:1:1:1");
    }

    [Fact]
    public async Task G4_ListaDeChaves_ContextoDePoolReaproveitado_ListaNaoVaza()
    {
        var database = await StartAsync();
        var logs = new LogSink();
        await using var services = new ServiceCollection()
            .AddLogging(l => l.AddProvider(logs))
            .AddWaybill(o =>
            {
                o.MaxPayloadBytes = 1024;
                o.AddMessage("billing.invoice-paid.v1", TestJson.Default.InvoicePaid);
            })
            .AddDbContextPool<AppDbContext>(o => o.UseNpgsql(database.ConnectionString), poolSize: 1)
            .AddWaybillOutbox<AppDbContext>()
            .BuildServiceProvider();

        foreach (var keys in new[] { new[] { "A", "B" }, new[] { "C" }, new[] { "B", "D" } })
        {
            await using var scope = services.CreateAsyncScope();
            var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var outbox = scope.ServiceProvider.GetRequiredService<IOutbox<AppDbContext>>();
            foreach (var key in keys)
                outbox.Enqueue(new InvoicePaid(Guid.NewGuid(), 1m), key);
            await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await AssertSavedKeysAsync(database, "A:1:1:1:1", "B:2:1:2:2", "C:1:1:1:1", "D:1:1:1:1");
        Assert.DoesNotContain(logs.Entries, e => e.Message.Contains("lacks the interceptor")); // ConfigureDbContext reaches pooled contexts
    }

    [Fact]
    public async Task G4_ListaDeChaves_DoisContextosNaMesmaTransacao_CadaUmTravaASuaLista()
    {
        var database = await StartAsync();
        await using var services = database.Services(sharedConnection: _ => new NpgsqlConnection(database.ConnectionString));
        await using var scope = services.CreateAsyncScope();
        var app = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var audit = scope.ServiceProvider.GetRequiredService<AuditDbContext>();
        var ct = TestContext.Current.CancellationToken;

        await using var transaction = await app.Database.BeginTransactionAsync(ct);
        await audit.Database.UseTransactionAsync(transaction.GetDbTransaction(), ct);
        var appOutbox = scope.ServiceProvider.GetRequiredService<IOutbox<AppDbContext>>();
        appOutbox.Enqueue(new InvoicePaid(Guid.NewGuid(), 1m), "A");
        appOutbox.Enqueue(new InvoicePaid(Guid.NewGuid(), 1m), "B");
        await app.SaveChangesAsync(ct);
        var auditOutbox = scope.ServiceProvider.GetRequiredService<IOutbox<AuditDbContext>>();
        auditOutbox.Enqueue(new AuditRecorded("paid"), "B");
        auditOutbox.Enqueue(new AuditRecorded("paid"), "C");
        await audit.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);

        await AssertSavedKeysAsync(database, "A:1:1:1:1", "B:2:1:2:2", "C:1:1:1:1");
    }

    // The interceptor runs once per SaveChanges, outside the execution strategy; the lists it set stay on the rows when the
    // strategy retries the save after a transient failure.
    [Fact]
    public async Task G4_ListaDeChaves_RetryDaEstrategiaDeExecucao_ListaContinuaNasLinhas()
    {
        var database = await StartAsync();
        var failOnce = new FailFirstSave();
        await using var services = database.Services(
            npgsql: o => o.EnableRetryOnFailure(3, TimeSpan.FromMilliseconds(10), null), interceptors: failOnce);
        await using var scope = services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var outbox = scope.ServiceProvider.GetRequiredService<IOutbox<AppDbContext>>();
        outbox.Enqueue(new InvoicePaid(Guid.NewGuid(), 1m), "A");
        outbox.Enqueue(new InvoicePaid(Guid.NewGuid(), 1m), "B");

        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        Assert.Equal(2, failOnce.Attempts);
        Assert.Equal(2, failOnce.ListsSeen); // the retry still sent both lists
        await AssertSavedKeysAsync(database, "A:1:1:1:1", "B:1:1:1:1");
    }

    private async Task<TestDatabase> StartAsync()
    {
        var database = await TestDatabase.CreateAsync(postgres);
        await KeyLockHarness.EnableOrderingAsync(database);
        return database;
    }

    private static async Task AssertSavedKeysAsync(TestDatabase database, params string[] expected)
    {
        Assert.Equal(expected, await KeyLockHarness.SequencesAsync(database));
        Assert.Equal(0, await database.ScalarAsync("SELECT count(*) FROM waybill.outbox WHERE lock_keys IS NOT NULL"));
    }

    // Fails the first SaveChanges command before it reaches the database, as a transient error; counts the lock_keys
    // parameters of the attempt that goes through.
    private sealed class FailFirstSave : DbCommandInterceptor
    {
        public int Attempts { get; private set; }
        public int ListsSeen { get; private set; }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (eventData.CommandSource == CommandSource.SaveChanges && command.CommandText.Contains("waybill.outbox"))
            {
                if (++Attempts == 1)
                    throw new NpgsqlException("injected transient failure", new TimeoutException());
                ListsSeen = command.Parameters.Cast<DbParameter>().Count(p => p.Value is string[] { Length: 2 });
            }
            return ValueTask.FromResult(result);
        }
    }
}
