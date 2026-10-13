using Npgsql;
using Waybill.EntityFrameworkCore.Dispatching;

namespace Waybill.Tests.Integration.G4;

// The head pointer (PLAN.md, "Ponteiro de cabeça"): ix_outbox_claimable holds only rows a claim can take, the unkeyed
// and unnumbered ones and the marked heads. Every SQL that walks it repeats the index's predicate word for word, or a
// generic plan (auto-prepare, PL/pgSQL) cannot prove the partial index applies and reads the whole queue. With one key
// dominating the statistics, the head is decided by min(sequence), which the planner answers from ix_outbox_key_sequence;
// a NOT EXISTS there was probed with a sequential scan of the outbox, row after row.
[Collection(PostgresCollection.Name)]
public sealed class G4_Ponteiro_PlanoGenerico_UsaOIndice(PostgresFixture postgres)
{
    public static TheoryData<string> Statements => ["ClaimOrderedSql", "ClaimSql", "OldestPendingAgeSql"];

    [Theory]
    [MemberData(nameof(Statements))]
    public async Task G4_Ponteiro_PlanoGenerico_UsaOIndice_SemSortNemVarreduraDaChave(string statement)
    {
        var database = await TestDatabase.CreateAsync(postgres);
        // One key dominating the table, half of it published; and rows without a key.
        await database.ExecuteAsync("""
            INSERT INTO waybill.outbox (id, type, key, key_hash, sequence, payload, content_type, status, published_at)
            SELECT ('00000000-0000-7000-8000-' || lpad(to_hex(g), 12, '0'))::uuid, 'billing.invoice-paid.v1', 'hot', 0, g,
                   '\x7b7d', 'application/json', CASE WHEN g <= 10000 THEN 'published' ELSE 'pending' END,
                   CASE WHEN g <= 10000 THEN clock_timestamp() END
            FROM generate_series(1, 20000) g;
            INSERT INTO waybill.outbox_keys VALUES ('hot', 20000);
            INSERT INTO waybill.outbox (id, type, key_hash, payload, content_type)
            SELECT gen_random_uuid(), 'billing.invoice-paid.v1', 0, '\x7b7d', 'application/json' FROM generate_series(1, 2000);
            """);
        await database.ExecuteAsync("VACUUM ANALYZE waybill.outbox");

        var plan = await GenericPlanAsync(database, statement);

        Assert.Contains("ix_outbox_claimable", plan);
        Assert.DoesNotContain("Sort", plan);
        Assert.DoesNotContain("Seq Scan on outbox h", plan); // the head
        Assert.DoesNotContain("Seq Scan on outbox s", plan); // the hot-key signal
    }

    private static async Task<string> GenericPlanAsync(TestDatabase database, string statement)
    {
        var (sql, types, arguments) = statement switch
        {
            "ClaimOrderedSql" => (OutboxStore.ClaimOrderedSql, "(text, interval, integer, integer)", "('instance', '30 seconds', 100, 4)"),
            "ClaimSql" => (OutboxStore.ClaimSql, "(text, interval, integer)", "('instance', '30 seconds', 100)"),
            _ => (OutboxStore.OldestPendingAgeSql, "", ""),
        };
        await using var connection = new NpgsqlConnection(database.ConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using (var prepare = new NpgsqlCommand($"SET plan_cache_mode = force_generic_plan; PREPARE claim{types} AS {sql}", connection))
            await prepare.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        await using var explain = new NpgsqlCommand($"EXPLAIN EXECUTE claim{arguments}", connection);
        await using var reader = await explain.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        var lines = new List<string>();
        while (await reader.ReadAsync(TestContext.Current.CancellationToken))
            lines.Add(reader.GetString(0));
        var plan = string.Join('\n', lines);
        TestContext.Current.TestOutputHelper?.WriteLine(plan);
        return plan;
    }
}
