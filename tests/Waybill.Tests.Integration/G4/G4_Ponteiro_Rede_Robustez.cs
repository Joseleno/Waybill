using System.Text.Json;
using Microsoft.Extensions.Logging;
using Npgsql;
using Waybill.EntityFrameworkCore.Dispatching;
using Waybill.Tests.Integration.G2;

namespace Waybill.Tests.Integration.G4;

// The repair sweep (ADR 0008, part 1b) in the conditions it meets in production (issue #21): prepared statements, whose
// generic plan cannot tell a null cursor from a key, and a sweep that fails.
[Collection(PostgresCollection.Name)]
public sealed class G4_Ponteiro_Rede_Robustez(PostgresFixture postgres)
{
    // A slice that starts after a key must seek to it in ix_outbox_key_sequence. As one statement with
    // "$1 IS NULL OR key > $1", a generic plan read the index from the first key on and filtered: every slice cost the
    // keys before it.
    [Fact]
    public async Task G4_Ponteiro_Rede_PlanoGenerico_UsaOIndice()
    {
        var ct = TestContext.Current.CancellationToken;
        var database = await TestDatabase.CreateAsync(postgres);
        await database.ExecuteAsync("""
            INSERT INTO waybill.outbox (id, type, key, key_hash, sequence, payload, content_type)
            SELECT ('00000000-0000-7000-8000-' || lpad(to_hex(k), 12, '0'))::uuid, 'billing.invoice-paid.v1',
                   'k-' || lpad(k::text, 6, '0'), 0, 1, '\x7b7d', 'application/json'
            FROM generate_series(1, 50000) k;
            """);
        await database.ExecuteAsync("VACUUM ANALYZE waybill.outbox");

        await using var connection = new NpgsqlConnection(database.ConnectionString);
        await connection.OpenAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        // The generic plan a prepared statement gets (auto-prepare): EXPLAIN over protocol parameters would plan with
        // them as constants.
        await using (var prepare = new NpgsqlCommand(
            "SET LOCAL plan_cache_mode = force_generic_plan; PREPARE repair(text, integer, integer, integer[]) AS " + OutboxStore.RepairHeadsAfterSql,
            connection, transaction))
            await prepare.ExecuteNonQueryAsync(ct);
        await using var explain = new NpgsqlCommand(
            "EXPLAIN (ANALYZE, BUFFERS, FORMAT JSON) EXECUTE repair('k-049990', 2000, 1, '{0}')", connection, transaction);
        var plan = JsonDocument.Parse((string)(await explain.ExecuteScalarAsync(ct))!).RootElement[0].GetProperty("Plan");
        await transaction.RollbackAsync(ct);

        var buffers = plan.GetProperty("Shared Hit Blocks").GetInt64() + plan.GetProperty("Shared Read Blocks").GetInt64();
        TestContext.Current.TestOutputHelper?.WriteLine($"slice after the 49990th of 50000 keys: {buffers} buffers");
        Assert.True(buffers < 500, $"{buffers} buffers for a slice of the last 10 keys");
    }

    // A sweep that fails (here, a trigger that refuses its UPDATE) is logged and skipped: the claim, the publication and
    // the marking of the cycle go on. Counted as a failed database cycle, it would back the whole dispatcher off.
    [Fact]
    public async Task G4_Ponteiro_Rede_ErroNaVarredura_NaoDerrubaOCiclo()
    {
        var ct = TestContext.Current.CancellationToken;
        var database = await TestDatabase.CreateAsync(postgres);
        await using var dataSource = NpgsqlDataSource.Create(database.ConnectionString);
        var transport = new FakeTransport();
        var logs = new LogSink();
        var dispatcher = OrderingHarness.Create(dataSource, transport, OrderingHarness.Options(database, partitions: 1), logs: logs);
        Assert.Null(await dispatcher.CheckOrderingAsync(atStartup: true, ct));
        await DispatcherHarness.EnqueueAsync(database, 1, key: _ => "broken");
        await DispatcherHarness.EnqueueAsync(database, 1, key: _ => "fine");
        await database.ExecuteAsync("""
            UPDATE waybill.outbox SET is_head = false WHERE key = 'broken';
            CREATE FUNCTION public.test_refuse_sweep() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN
                IF current_query() LIKE '%WITH RECURSIVE walk%' THEN
                    RAISE EXCEPTION 'the sweep is refused by the test';
                END IF;
                RETURN NEW;
            END $$;
            CREATE TRIGGER test_refuse_sweep BEFORE UPDATE ON waybill.outbox FOR EACH ROW EXECUTE FUNCTION public.test_refuse_sweep();
            """);

        var cycle = await dispatcher.RunOnceAsync(ct); // takes the partition, sweeps (and fails), claims

        Assert.Equal(["fine"], transport.Received.Select(m => m.Key));
        Assert.Equal(DispatchOutcome.Progress, cycle.Outcome);
        Assert.Contains(logs.Entries, e => e.Level == LogLevel.Error && e.Message.Contains("sweep"));
    }
}
