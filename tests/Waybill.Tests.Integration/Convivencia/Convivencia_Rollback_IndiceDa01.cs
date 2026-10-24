using System.Text;
using Npgsql;

namespace Waybill.Tests.Integration.Convivencia;

// OPERATIONS.md, "Upgrading and rolling back": the v0.2 claim index keeps only rows the 0.2 claim can take (its predicate
// has is_head), so the 0.1 claim cannot use it, and after a rollback it would read the published rows retention keeps to
// reach the pending ones. The SQL OPERATIONS.md gives builds the index 0.1 had; run as written, the 0.1 claim, read from
// the 0.1 package, uses it in a generic plan (auto-prepare).
[Collection(PostgresCollection.Name)]
public sealed class Convivencia_Rollback_IndiceDa01(PostgresFixture postgres)
{
    [Fact]
    public async Task Convivencia_Rollback_IndiceDa01_ClaimDa01UsaOIndice()
    {
        var database = await TestDatabase.CreateAsync(postgres);
        // Retention keeps a week of published rows, older (lower UUIDv7) than the pending ones.
        await database.ExecuteAsync("""
            INSERT INTO waybill.outbox (id, type, key_hash, payload, content_type, status, published_at)
            SELECT ('00000000-0000-7000-8000-' || lpad(to_hex(g), 12, '0'))::uuid, 'billing.invoice-paid.v1', 0, '\x7b7d',
                   'application/json', CASE WHEN g <= 20000 THEN 'published' ELSE 'pending' END,
                   CASE WHEN g <= 20000 THEN clock_timestamp() END
            FROM generate_series(1, 20100) g;
            """);
        await database.ExecuteAsync("VACUUM ANALYZE waybill.outbox");

        string claim;
        await using (var host = await LegacyHost.StartAsync("claim-sql"))
        {
            claim = Encoding.UTF8.GetString(Convert.FromBase64String(await host.WaitForAsync("CLAIM-SQL ", TimeSpan.FromSeconds(30))));
            await host.ExitedAsync();
        }

        var before = await GenericPlanAsync(database, claim);
        Assert.DoesNotContain("ix_outbox_claimable", before); // the v0.2 index is no use to it

        await database.ExecuteAsync(OperationsDoc.Sql("rollback-claim-index"));

        var after = await GenericPlanAsync(database, claim);
        Assert.Contains("ix_outbox_claimable_v01", after);
        Assert.DoesNotContain("Seq Scan", after);
    }

    private static async Task<string> GenericPlanAsync(TestDatabase database, string claim)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var connection = new NpgsqlConnection(database.ConnectionString);
        await connection.OpenAsync(ct);
        await using (var prepare = new NpgsqlCommand($"SET plan_cache_mode = force_generic_plan; PREPARE claim(text, interval, integer) AS {claim}", connection))
            await prepare.ExecuteNonQueryAsync(ct);
        await using var explain = new NpgsqlCommand("EXPLAIN EXECUTE claim('instance', '30 seconds', 100)", connection);
        await using var reader = await explain.ExecuteReaderAsync(ct);
        var lines = new List<string>();
        while (await reader.ReadAsync(ct))
            lines.Add(reader.GetString(0));
        var plan = string.Join('\n', lines);
        TestContext.Current.TestOutputHelper?.WriteLine(plan);
        return plan;
    }
}
