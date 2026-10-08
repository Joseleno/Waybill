using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Waybill.EntityFrameworkCore;

namespace Waybill.Tests.Integration.G4;

// While ordering by key is on, each keyed row locks its key's counter until commit (ADR 0008). EF inserts a SaveChanges'
// rows in id order, so two transactions enqueuing K1 and K2 in opposite orders would each hold one key and wait for the
// other. The key list makes every transaction lock all its keys, in one order, at its first outbox row. Each scenario
// forces the dangerous interleaving with a barrier and has a negative control: the same interleaving without the list
// (or with the lock placed where the first design put it) deadlocks every time.
[Collection(PostgresCollection.Name)]
public sealed class G4_ListaDeChaves_SemDeadlock(PostgresFixture postgres)
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task G4_ListaDeChaves_OrdensOpostas_SemDeadlock(bool keysExist)
    {
        var database = await TestDatabase.CreateAsync(postgres);
        await KeyLockHarness.EnableOrderingAsync(database);
        if (keysExist)
            await database.ExecuteAsync("INSERT INTO waybill.outbox_keys VALUES ('K1', 0), ('K2', 0)");
        await using var barrier = await Barrier.InstallAsync(database, "waybill.outbox", "INSERT");

        var first = KeyLockHarness.EnqueueAsync(database, KeyLockHarness.BarrierApp, "K1", "K2");
        var second = KeyLockHarness.EnqueueAsync(database, KeyLockHarness.BarrierApp, "K2", "K1");
        // One holds both keys and waits at the barrier; the other waits for those keys, holding none.
        await barrier.WaitForAsync(atBarrier: 1, onLocks: 1);
        await barrier.ReleaseAsync();
        await Task.WhenAll(first, second);

        Assert.Equal(["K1:2:1:2:2", "K2:2:1:2:2"], await KeyLockHarness.SequencesAsync(database));
    }

    // The trigger marks the list it locked so later rows of the same SaveChanges skip it. The mark lasts the transaction
    // only: on a pooled session, the next transaction with the same list must lock it again.
    [Fact]
    public async Task G4_ListaDeChaves_MarcaDaListaNaoSobreviveATransacao()
    {
        var database = await TestDatabase.CreateAsync(postgres);
        await KeyLockHarness.EnableOrderingAsync(database);
        await using var first = new NpgsqlConnection(KeyLockHarness.ConnectionString(database, KeyLockHarness.BarrierApp));
        await using var second = new NpgsqlConnection(KeyLockHarness.ConnectionString(database, KeyLockHarness.BarrierApp));
        await first.OpenAsync(TestContext.Current.CancellationToken);
        await second.OpenAsync(TestContext.Current.CancellationToken);
        foreach (var session in new[] { first, second }) // an earlier transaction on each session, with the same list
        {
            await using var earlier = await session.BeginTransactionAsync(TestContext.Current.CancellationToken);
            await KeyLockHarness.InsertAsync(session, "K1", ["K1", "K2"]);
            await earlier.CommitAsync(TestContext.Current.CancellationToken);
        }
        await using var barrier = await Barrier.InstallAsync(database, "waybill.outbox", "INSERT");

        static async Task<PostgresException?> Enqueue(NpgsqlConnection session, params string[] keys)
        {
            await using var transaction = await session.BeginTransactionAsync();
            try
            {
                foreach (var key in keys)
                    await KeyLockHarness.InsertAsync(session, key, ["K1", "K2"]);
                await transaction.CommitAsync();
                return null;
            }
            catch (PostgresException error)
            {
                return error;
            }
        }

        var a = Task.Run(() => Enqueue(first, "K1", "K2"));
        var b = Task.Run(() => Enqueue(second, "K2", "K1"));
        await barrier.WaitForAsync(atBarrier: 1, onLocks: 1);
        await barrier.ReleaseAsync();

        Assert.Equal([null, null], await Task.WhenAll(a, b));
    }

    [Fact]
    public async Task G4_ListaDeChaves_OrdensOpostasSemALista_Deadlock()
    {
        var database = await TestDatabase.CreateAsync(postgres);
        await KeyLockHarness.EnableOrderingAsync(database);
        await using var barrier = await Barrier.InstallAsync(database, "waybill.outbox", "INSERT");

        async Task<PostgresException?> Enqueue(params string[] keys)
        {
            await using var connection = new NpgsqlConnection(KeyLockHarness.ConnectionString(database, KeyLockHarness.BarrierApp));
            await connection.OpenAsync();
            await using var transaction = await connection.BeginTransactionAsync();
            try
            {
                foreach (var key in keys)
                    await KeyLockHarness.InsertAsync(connection, key); // no lock_keys: each row locks only its own key
                await transaction.CommitAsync();
                return null;
            }
            catch (PostgresException error)
            {
                return error;
            }
        }

        var first = Task.Run(() => Enqueue("K1", "K2"));
        var second = Task.Run(() => Enqueue("K2", "K1"));
        await barrier.WaitForAsync(atBarrier: 2, onLocks: 0); // each holds its first key
        await barrier.ReleaseAsync();
        var errors = await Task.WhenAll(first, second);

        Assert.Equal([PostgresErrorCodes.DeadlockDetected], errors.Where(e => e is not null).Select(e => e!.SqlState));
    }

    // EF writes the application's rows before the outbox's, so a SaveChanges of one key locks the application row and
    // then the key. The list is locked at the first outbox row, after the application rows too: a SaveChanges of two
    // keys keeps that order.
    [Fact]
    public async Task G4_ListaDeChaves_UmaChaveEDuasNaMesmaLinhaDaAplicacao_SemDeadlock()
    {
        var database = await TestDatabase.CreateAsync(postgres);
        await KeyLockHarness.EnableOrderingAsync(database);
        var invoice = await KeyLockHarness.SeedInvoiceAsync(database);
        await using var barrier = await Barrier.InstallAsync(database, "invoices", "UPDATE");

        Task Pay(string applicationName, params string[] keys) => Task.Run(async () =>
        {
            await using var services = database.Services(sharedConnection: _ => new NpgsqlConnection(KeyLockHarness.ConnectionString(database, applicationName)));
            await using var scope = services.CreateAsyncScope();
            var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var outbox = scope.ServiceProvider.GetRequiredService<IOutbox<AppDbContext>>();
            var row = await context.Invoices.SingleAsync(i => i.Id == invoice);
            row.Amount += 1;
            foreach (var key in keys)
                outbox.Enqueue(new InvoicePaid(invoice, row.Amount), key);
            await context.SaveChangesAsync();
        });

        var oneKey = Pay(KeyLockHarness.BarrierApp, "K1");   // holds the invoice, stops before its outbox row
        await barrier.WaitForAsync(atBarrier: 1, onLocks: 0);
        var twoKeys = Pay("app", "K1", "K2");                // waits for the invoice, holding no key
        await barrier.WaitForAsync(atBarrier: 1, onLocks: 1);
        await barrier.ReleaseAsync();
        await Task.WhenAll(oneKey, twoKeys);

        Assert.Equal(["K1:2:1:2:2", "K2:1:1:1:1"], await KeyLockHarness.SequencesAsync(database));
    }

    // The first design locked the list at the start of the SaveChanges command, before the application's rows.
    [Fact]
    public async Task G4_ListaDeChaves_TravaAntepostaAoComando_Deadlock()
    {
        var database = await TestDatabase.CreateAsync(postgres);
        await KeyLockHarness.EnableOrderingAsync(database);
        var invoice = await KeyLockHarness.SeedInvoiceAsync(database);
        await using var barrier = await Barrier.InstallAsync(database, "invoices", "UPDATE");
        var updateInvoice = $"""UPDATE invoices SET "Amount" = "Amount" + 1 WHERE "Id" = '{invoice}'""";

        async Task<PostgresException?> Run(string applicationName, Func<NpgsqlConnection, Task> work)
        {
            await using var connection = new NpgsqlConnection(KeyLockHarness.ConnectionString(database, applicationName));
            await connection.OpenAsync();
            await using var transaction = await connection.BeginTransactionAsync();
            try
            {
                await work(connection);
                await transaction.CommitAsync();
                return null;
            }
            catch (PostgresException error)
            {
                return error;
            }
        }

        var oneKey = Task.Run(() => Run(KeyLockHarness.BarrierApp, async c =>
        {
            await KeyLockHarness.ExecuteAsync(c, updateInvoice); // holds the invoice, stops at the barrier
            await KeyLockHarness.InsertAsync(c, "K1");
        }));
        await barrier.WaitForAsync(atBarrier: 1, onLocks: 0);
        var twoKeys = Task.Run(() => Run("app", async c =>
        {
            await KeyLockHarness.ExecuteAsync(c, """
                INSERT INTO waybill.outbox_keys AS k (key, seq) SELECT x, 0 FROM unnest('{K1,K2}'::text[]) x ORDER BY x
                ON CONFLICT (key) DO UPDATE SET seq = k.seq
                """);                                        // the keys first...
            await KeyLockHarness.ExecuteAsync(c, updateInvoice); // ...then the invoice
            await KeyLockHarness.InsertAsync(c, "K1", ["K1", "K2"]);
            await KeyLockHarness.InsertAsync(c, "K2", ["K1", "K2"]);
        }));
        await barrier.WaitForAsync(atBarrier: 1, onLocks: 1);
        await barrier.ReleaseAsync();
        var errors = await Task.WhenAll(oneKey, twoKeys);

        Assert.Equal([PostgresErrorCodes.DeadlockDetected], errors.Where(e => e is not null).Select(e => e!.SqlState));
    }
}
