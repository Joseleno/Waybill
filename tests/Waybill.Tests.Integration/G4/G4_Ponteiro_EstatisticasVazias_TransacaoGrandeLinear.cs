using Npgsql;

namespace Waybill.Tests.Integration.G4;

// A table just created (or just truncated) has no statistics, and a trigger's query plan is made once per session on
// them. The sondagem (PLAN.md) saw the head probe become a sequential scan of the outbox, once per inserted row: a
// transaction of 50 thousand rows took 18.9 s, one of 100 thousand never finished. The trigger's queries must descend
// ix_outbox_key_sequence whatever the statistics say; the oracle is the transaction's own count of sequential scans.
[Collection(PostgresCollection.Name)]
public sealed class G4_Ponteiro_EstatisticasVazias_TransacaoGrandeLinear(PostgresFixture postgres)
{
    [Fact]
    public async Task G4_Ponteiro_EstatisticasVazias_TransacaoGrande_SemVarreduraPorLinha()
    {
        var ct = TestContext.Current.CancellationToken;
        var database = await TestDatabase.CreateAsync(postgres);
        await KeyLockHarness.EnableOrderingAsync(database);
        await using var connection = new NpgsqlConnection(database.ConnectionString);
        await connection.OpenAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);

        // 50 thousand rows over 5 thousand keys, interleaved, so no row follows its key's previous one.
        await using (var insert = new NpgsqlCommand("""
            INSERT INTO waybill.outbox (id, type, key, key_hash, payload, content_type)
            SELECT ('00000000-0000-7000-8000-' || lpad(to_hex(g), 12, '0'))::uuid, 'billing.invoice-paid.v1',
                   'k-' || (g % 5000), 0, '\x7b7d', 'application/json'
            FROM generate_series(1, 50000) g
            """, connection, transaction))
            await insert.ExecuteNonQueryAsync(ct);
        // The checks deferred to the commit run now, inside the transaction, so they count too.
        await using (var immediate = new NpgsqlCommand("SET CONSTRAINTS ALL IMMEDIATE", connection, transaction))
            await immediate.ExecuteNonQueryAsync(ct);
        await using var scans = new NpgsqlCommand(
            "SELECT seq_scan FROM pg_stat_xact_user_tables WHERE schemaname = 'waybill' AND relname = 'outbox'", connection, transaction);
        var sequentialScans = (long)(await scans.ExecuteScalarAsync(ct))!;
        await transaction.CommitAsync(ct);

        Assert.True(sequentialScans < 10, $"{sequentialScans} sequential scans of the outbox for 50 thousand inserted rows");
        Assert.Equal(5000, await database.ScalarAsync("SELECT count(*) FROM waybill.outbox WHERE is_head"));
    }
}
