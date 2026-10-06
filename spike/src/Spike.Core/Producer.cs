using Npgsql;
using NpgsqlTypes;

namespace Spike.Core;

public static class Producer
{
    // Carga em massa para os testes de claim: sequence já atribuída por chave, na ordem de inserção.
    public static async Task BulkInsertAsync(
        NpgsqlDataSource ds, int count, int keyCount, int partitions, int payloadBytes = 64, CancellationToken ct = default)
    {
        var payload = new byte[payloadBytes];
        var seqByKey = new long[keyCount];

        await using var conn = await ds.OpenConnectionAsync(ct);
        await using (var writer = await conn.BeginBinaryImportAsync(
            "COPY outbox (id, key, partition, sequence, payload) FROM STDIN (FORMAT BINARY)", ct))
        {
            for (var i = 0; i < count; i++)
            {
                var k = i % keyCount;
                var key = $"k{k}";
                await writer.StartRowAsync(ct);
                await writer.WriteAsync(Guid.CreateVersion7(), NpgsqlDbType.Uuid, ct);
                await writer.WriteAsync(key, NpgsqlDbType.Text, ct);
                await writer.WriteAsync(Schema.PartitionOf(key, partitions), NpgsqlDbType.Integer, ct);
                await writer.WriteAsync(++seqByKey[k], NpgsqlDbType.Bigint, ct);
                await writer.WriteAsync(payload, NpgsqlDbType.Bytea, ct);
            }
            await writer.CompleteAsync(ct);
        }

        await using var keys = conn.CreateCommand();
        keys.CommandText = "INSERT INTO outbox_keys (key, seq) SELECT key, max(sequence) FROM outbox GROUP BY key";
        await keys.ExecuteNonQueryAsync(ct);
        await using var analyze = conn.CreateCommand();
        analyze.CommandText = "ANALYZE outbox";
        await analyze.ExecuteNonQueryAsync(ct);
    }

    // Caminho do produtor real (v0.2): incrementa o contador da chave dentro da transação de negócio.
    // O lock da linha em outbox_keys fica preso até o commit — é isso que a pergunta 5 mede.
    public static async Task<long> NextSequenceAsync(NpgsqlConnection conn, NpgsqlTransaction tx, string key, CancellationToken ct = default)
    {
        await using var cmd = new NpgsqlCommand("""
            INSERT INTO outbox_keys (key, seq) VALUES (@k, 1)
            ON CONFLICT (key) DO UPDATE SET seq = outbox_keys.seq + 1
            RETURNING seq
            """, conn, tx);
        cmd.Parameters.AddWithValue("k", key);
        return (long)(await cmd.ExecuteScalarAsync(ct))!;
    }

    // Variante fundida: contador e INSERT numa só ida ao banco, no fim da transação (onde o SavingChanges roda).
    public static async Task InsertWithNextSequenceAsync(
        NpgsqlConnection conn, NpgsqlTransaction tx, string key, int partitions, CancellationToken ct = default)
    {
        await using var cmd = new NpgsqlCommand("""
            WITH s AS (
                INSERT INTO outbox_keys (key, seq) VALUES (@k, 1)
                ON CONFLICT (key) DO UPDATE SET seq = outbox_keys.seq + 1
                RETURNING seq
            )
            INSERT INTO outbox (id, key, partition, sequence, payload)
            SELECT @id, @k, @p, s.seq, @b FROM s
            """, conn, tx);
        cmd.Parameters.AddWithValue("id", Guid.CreateVersion7());
        cmd.Parameters.AddWithValue("k", key);
        cmd.Parameters.AddWithValue("p", Schema.PartitionOf(key, partitions));
        cmd.Parameters.AddWithValue("b", new byte[64]);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public static async Task InsertAsync(
        NpgsqlConnection conn, NpgsqlTransaction tx, string key, long sequence, int partitions, CancellationToken ct = default)
    {
        await using var cmd = new NpgsqlCommand("""
            INSERT INTO outbox (id, key, partition, sequence, payload) VALUES (@id, @k, @p, @s, @b)
            """, conn, tx);
        cmd.Parameters.AddWithValue("id", Guid.CreateVersion7());
        cmd.Parameters.AddWithValue("k", key);
        cmd.Parameters.AddWithValue("p", Schema.PartitionOf(key, partitions));
        cmd.Parameters.AddWithValue("s", sequence);
        cmd.Parameters.AddWithValue("b", new byte[64]);
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
