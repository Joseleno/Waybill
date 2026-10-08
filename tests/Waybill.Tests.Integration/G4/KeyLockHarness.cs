using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Waybill.EntityFrameworkCore;

namespace Waybill.Tests.Integration.G4;

/// <summary>Enqueuing while ordering by key is on, with interleavings forced by a barrier instead of left to chance.</summary>
public static class KeyLockHarness
{
    /// <summary>Sessions with this application name stop at the barrier; the others never do.</summary>
    public const string BarrierApp = "barrier";

    /// <summary>Turns ordering on as the first ordering dispatcher would: the database starts numbering keyed rows.</summary>
    public static Task EnableOrderingAsync(TestDatabase database) =>
        database.ExecuteAsync("INSERT INTO waybill.settings (id, partitions, partition_lease) VALUES (1, 4, interval '60 seconds')");

    /// <summary>A connection string naming the session, with a lock timeout so a broken test fails instead of hanging.</summary>
    public static string ConnectionString(TestDatabase database, string applicationName) =>
        new NpgsqlConnectionStringBuilder(database.ConnectionString)
        {
            ApplicationName = applicationName,
            Options = "-c lock_timeout=10000",
        }.ConnectionString;

    /// <summary>Enqueues one message per key and saves them in one SaveChanges, inserted in this order (ids follow enqueue order).</summary>
    public static Task EnqueueAsync(TestDatabase database, string applicationName, params string[] keys) =>
        Task.Run(async () =>
        {
            await using var services = database.Services(sharedConnection: _ => new NpgsqlConnection(ConnectionString(database, applicationName)));
            await using var scope = services.CreateAsyncScope();
            var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var outbox = scope.ServiceProvider.GetRequiredService<IOutbox<AppDbContext>>();
            foreach (var key in keys)
                outbox.Enqueue(new InvoicePaid(Guid.NewGuid(), 1m), key);
            await context.SaveChangesAsync();
        });

    /// <summary>What the application's INSERT does, written by hand: one outbox row with a key and, optionally, the key list.</summary>
    public static async Task InsertAsync(NpgsqlConnection connection, string key, string[]? lockKeys = null)
    {
        await using var command = new NpgsqlCommand("""
            INSERT INTO waybill.outbox (id, type, key, key_hash, payload, content_type, lock_keys)
            VALUES (gen_random_uuid(), 'billing.invoice-paid.v1', $1, 0, '\x00', 'application/json', $2)
            """, connection);
        command.Parameters.Add(new NpgsqlParameter { Value = key });
        command.Parameters.Add(new NpgsqlParameter { Value = (object?)lockKeys ?? DBNull.Value, NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.Text });
        await command.ExecuteNonQueryAsync();
    }

    public static async Task ExecuteAsync(NpgsqlConnection connection, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    /// <summary>An invoice row both transactions will update: the application's own lock.</summary>
    public static async Task<Guid> SeedInvoiceAsync(TestDatabase database)
    {
        var id = Guid.NewGuid();
        await database.ExecuteAsync($"""INSERT INTO invoices ("Id", "Number", "Amount") VALUES ('{id}', 'INV-{id:N}', 0)""");
        return id;
    }

    /// <summary>Per key: how many rows, the lowest and highest sequence, and the counter, as "key:count:min:max:counter".</summary>
    public static async Task<string[]> SequencesAsync(TestDatabase database)
    {
        await using var connection = new NpgsqlConnection(database.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("""
            SELECT k.key || ':' || count(o.id) || ':' || coalesce(min(o.sequence), 0) || ':' || coalesce(max(o.sequence), 0) || ':' || k.seq
            FROM waybill.outbox_keys k LEFT JOIN waybill.outbox o ON o.key = k.key
            GROUP BY k.key, k.seq ORDER BY k.key COLLATE "C"
            """, connection);
        var rows = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            rows.Add(reader.GetString(0));
        return [.. rows];
    }

    /// <summary>Waits until <paramref name="atBarrier"/> sessions wait at the barrier and <paramref name="onLocks"/> wait on a row or transaction lock.</summary>
    public static async Task WaitForAsync(NpgsqlConnection observer, int atBarrier, int onLocks)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        var seen = (-1L, -1L);
        while (DateTime.UtcNow < deadline)
        {
            await using var command = new NpgsqlCommand("""
                SELECT count(*) FILTER (WHERE wait_event = 'advisory'),
                       count(*) FILTER (WHERE wait_event_type = 'Lock' AND wait_event <> 'advisory')
                FROM pg_stat_activity
                WHERE datname = current_database() AND pid <> pg_backend_pid() AND state = 'active'
                """, observer);
            await using (var reader = await command.ExecuteReaderAsync())
            {
                await reader.ReadAsync();
                seen = (reader.GetInt64(0), reader.GetInt64(1));
            }
            if (seen == (atBarrier, onLocks))
                return;
            await Task.Delay(20);
        }
        throw new TimeoutException($"Expected {atBarrier} session(s) at the barrier and {onLocks} on locks; saw {seen.Item1} and {seen.Item2}.");
    }

    /// <inheritdoc cref="WaitForAsync(NpgsqlConnection, int, int)"/>
    public static async Task WaitForAsync(TestDatabase database, int atBarrier, int onLocks)
    {
        await using var observer = new NpgsqlConnection(database.ConnectionString);
        await observer.OpenAsync();
        await WaitForAsync(observer, atBarrier, onLocks);
    }

    /// <summary>The PostgreSQL error inside an EF or Npgsql exception, if any.</summary>
    public static PostgresException? PostgresError(Exception? exception)
    {
        for (var e = exception; e is not null; e = e.InnerException)
        {
            if (e is PostgresException postgres)
                return postgres;
        }
        return null;
    }
}

/// <summary>
/// Stops each named session once, right after its first row of <c>table</c> is written (still inside its transaction,
/// so its locks are held), until the test lets go. The test waits until it sees, in pg_stat_activity, every session
/// where it should be, before letting go: the interleaving is forced, not hoped for.
/// </summary>
public sealed class Barrier : IAsyncDisposable
{
    private const long LockId = 4242;
    private readonly TestDatabase _database;
    private readonly NpgsqlConnection _holder;

    private Barrier(TestDatabase database, NpgsqlConnection holder)
    {
        _database = database;
        _holder = holder;
    }

    /// <param name="database">The test database.</param>
    /// <param name="table">The table whose first written row stops the session: <c>waybill.outbox</c> or <c>invoices</c>.</param>
    /// <param name="operation"><c>INSERT</c> or <c>UPDATE</c>.</param>
    public static async Task<Barrier> InstallAsync(TestDatabase database, string table, string operation)
    {
        await database.ExecuteAsync($"""
            CREATE OR REPLACE FUNCTION public.test_barrier() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN
                IF current_setting('application_name') = '{KeyLockHarness.BarrierApp}'
                   AND current_setting('test.barrier_passed', true) IS DISTINCT FROM 'y' THEN
                    PERFORM set_config('test.barrier_passed', 'y', true);
                    PERFORM pg_advisory_xact_lock_shared({LockId});
                END IF;
                RETURN NULL;
            END $$;
            CREATE TRIGGER test_barrier AFTER {operation} ON {table} FOR EACH ROW EXECUTE FUNCTION public.test_barrier();
            """);
        var holder = new NpgsqlConnection(database.ConnectionString);
        await holder.OpenAsync();
        await KeyLockHarness.ExecuteAsync(holder, $"SELECT pg_advisory_lock({LockId})");
        return new Barrier(database, holder);
    }

    /// <summary>Waits until <paramref name="atBarrier"/> sessions wait at the barrier and <paramref name="onLocks"/> wait on a row or transaction lock.</summary>
    public Task WaitForAsync(int atBarrier, int onLocks) => KeyLockHarness.WaitForAsync(_holder, atBarrier, onLocks);

    public Task ReleaseAsync() => KeyLockHarness.ExecuteAsync(_holder, $"SELECT pg_advisory_unlock({LockId})");

    public async ValueTask DisposeAsync()
    {
        await _holder.DisposeAsync();
        await _database.ExecuteAsync("DROP TRIGGER IF EXISTS test_barrier ON waybill.outbox; DROP TRIGGER IF EXISTS test_barrier ON invoices");
    }
}
