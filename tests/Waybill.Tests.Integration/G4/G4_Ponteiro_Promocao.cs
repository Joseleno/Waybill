using Npgsql;
using Waybill.Tests.Integration.G2;

namespace Waybill.Tests.Integration.G4;

// The head pointer's promotion (ADR 0008, part 2): when a row of a key finishes, the least unfinished one is marked,
// also when another transaction finishes or deletes a row of the same key at the same time.
[Collection(PostgresCollection.Name)]
public sealed class G4_Ponteiro_Promocao(PostgresFixture postgres)
{
    // Two finishes of one key at once (a zombie still holding m2's fence and the partition's new owner publishing m1, or an
    // operator's DELETE or release of m2). The promotion of m1 aims at m2, waits for the other transaction, and must not
    // stop there: m2 is finished by then, and m3 is the key's head. Forced with row locks, in both orders.
    [Theory]
    [InlineData("other finishes m2 first", "UPDATE waybill.outbox SET status = 'published' WHERE key = 'K' AND sequence = 2", false, false)]
    [InlineData("m1 finishes first", "UPDATE waybill.outbox SET status = 'published' WHERE key = 'K' AND sequence = 2", true, false)]
    [InlineData("m2 a false positive", "UPDATE waybill.outbox SET status = 'published' WHERE key = 'K' AND sequence = 2", false, true)]
    // m2 marked and finished while m1 finishes: m2's promotion must leave the key to whoever finishes m1, not wait for
    // m1, which waits for m2 (a deadlock).
    [InlineData("m2 a false positive, m1 first", "UPDATE waybill.outbox SET status = 'published' WHERE key = 'K' AND sequence = 2", true, true)]
    [InlineData("m2 deleted", "DELETE FROM waybill.outbox WHERE key = 'K' AND sequence = 2", false, false)]
    [InlineData("m2 released from the DLQ", "UPDATE waybill.outbox SET status = 'released', released_at = clock_timestamp(), released_by = 'ops' WHERE key = 'K' AND sequence = 2", false, false)]
    public async Task G4_Ponteiro_TerminacoesConcorrentesNaMesmaChave_PromoveAMenor(string why, string finishM2, bool m1First, bool m2FalsePositive)
    {
        var ct = TestContext.Current.CancellationToken;
        var database = await TestDatabase.CreateAsync(postgres);
        await KeyLockHarness.EnableOrderingAsync(database);
        await DispatcherHarness.EnqueueAsync(database, 4, key: _ => "K"); // one transaction: m1 marked, m2..m4 not
        if (why.Contains("DLQ"))
            await database.ExecuteAsync("UPDATE waybill.outbox SET status = 'dlq', dlq_reason = 'test' WHERE key = 'K' AND sequence = 2");
        if (m2FalsePositive)
            await database.ExecuteAsync("UPDATE waybill.outbox SET is_head = true WHERE key = 'K' AND sequence = 2");
        const string finishM1 = "UPDATE waybill.outbox SET status = 'published' WHERE key = 'K' AND sequence = 1";

        await using var first = new NpgsqlConnection(KeyLockHarness.ConnectionString(database, "first"));
        await using var second = new NpgsqlConnection(KeyLockHarness.ConnectionString(database, "second"));
        await first.OpenAsync(ct);
        await second.OpenAsync(ct);
        await using var holding = await first.BeginTransactionAsync(ct);
        await KeyLockHarness.ExecuteAsync(first, m1First ? finishM1 : finishM2);
        var waiting = KeyLockHarness.ExecuteAsync(second, m1First ? finishM2 : finishM1);
        // The second waits for the first's row lock; a promotion that does not look at the row it aims at may not wait at
        // all, and then only the end state tells.
        await Task.WhenAny(waiting, KeyLockHarness.WaitForAsync(database, atBarrier: 0, onLocks: 1));
        await holding.CommitAsync(ct);
        await waiting;

        Assert.Equal(["3:True", "4:False"], await UnfinishedAsync(database));
    }

    // Both finishes paused after their own row and before their promotion (a trigger named to fire just before it), then
    // let go together: m1's promotion aims at m2, held by the other; m2 (marked, a false positive) must not aim back at
    // m1, still unfinished in its snapshot, or each waits for the other.
    [Fact]
    public async Task G4_Ponteiro_TerminacoesSimultaneas_SemDeadlock()
    {
        var ct = TestContext.Current.CancellationToken;
        var database = await TestDatabase.CreateAsync(postgres);
        await KeyLockHarness.EnableOrderingAsync(database);
        await DispatcherHarness.EnqueueAsync(database, 4, key: _ => "K");
        await database.ExecuteAsync("""
            UPDATE waybill.outbox SET is_head = true WHERE key = 'K' AND sequence = 2;
            CREATE FUNCTION public.test_pause() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN
                IF current_setting('application_name') IN ('first', 'second') THEN
                    PERFORM pg_advisory_lock_shared(4545);
                    PERFORM pg_advisory_unlock_shared(4545);
                END IF;
                RETURN NULL;
            END $$;
            CREATE TRIGGER outbox_head_a_test_pause AFTER UPDATE OF status ON waybill.outbox FOR EACH ROW EXECUTE FUNCTION public.test_pause();
            """);
        await using var holder = new NpgsqlConnection(database.ConnectionString);
        await holder.OpenAsync(ct);
        await KeyLockHarness.ExecuteAsync(holder, "SELECT pg_advisory_lock(4545)");
        await using var first = new NpgsqlConnection(KeyLockHarness.ConnectionString(database, "first"));
        await using var second = new NpgsqlConnection(KeyLockHarness.ConnectionString(database, "second"));
        await first.OpenAsync(ct);
        await second.OpenAsync(ct);

        var m1 = KeyLockHarness.ExecuteAsync(first, "UPDATE waybill.outbox SET status = 'published' WHERE key = 'K' AND sequence = 1");
        var m2 = KeyLockHarness.ExecuteAsync(second, "UPDATE waybill.outbox SET status = 'published' WHERE key = 'K' AND sequence = 2");
        await KeyLockHarness.WaitForAsync(holder, atBarrier: 2, onLocks: 0);
        await KeyLockHarness.ExecuteAsync(holder, "SELECT pg_advisory_unlock(4545)");
        await Task.WhenAll(m1, m2);

        Assert.Equal(["3:True", "4:False"], await UnfinishedAsync(database));
    }

    // A series published in one statement (the head and its successors of one batch): one promotion for the key, from
    // its marked head, not one per row walking the rows the statement just published.
    [Fact]
    public async Task G4_Ponteiro_FinishComSerie_UmaPromocaoPorChave()
    {
        var ct = TestContext.Current.CancellationToken;
        var database = await TestDatabase.CreateAsync(postgres);
        await KeyLockHarness.EnableOrderingAsync(database);
        await DispatcherHarness.EnqueueAsync(database, 110, key: _ => "H");

        await using var connection = new NpgsqlConnection(database.ConnectionString);
        await connection.OpenAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        await KeyLockHarness.ExecuteAsync(connection, "SET LOCAL track_functions = 'all'");
        await KeyLockHarness.ExecuteAsync(connection, "UPDATE waybill.outbox SET status = 'published' WHERE key = 'H' AND sequence <= 100");
        await using var calls = new NpgsqlCommand(
            "SELECT coalesce(sum(calls), 0)::bigint FROM pg_stat_xact_user_functions WHERE schemaname = 'waybill' AND funcname = 'outbox_head_promote'", connection, transaction);
        var promotions = (long)(await calls.ExecuteScalarAsync(ct))!;
        await transaction.CommitAsync(ct);

        Assert.Equal(1, promotions);
        Assert.Equal(101, await database.ScalarAsync("SELECT sequence FROM waybill.outbox WHERE key = 'H' AND is_head AND status = 'pending'"));
        Assert.Equal(1, await database.ScalarAsync("SELECT count(*) FROM waybill.outbox WHERE key = 'H' AND is_head AND status = 'pending'"));
    }

    private static async Task<List<string>> UnfinishedAsync(TestDatabase database)
    {
        await using var connection = new NpgsqlConnection(database.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT sequence || ':' || coalesce(is_head::text, 'null') FROM waybill.outbox WHERE status IN ('pending', 'claimed', 'dlq') ORDER BY sequence", connection);
        var rows = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            rows.Add(reader.GetString(0).Replace("true", "True").Replace("false", "False"));
        return rows;
    }
}
