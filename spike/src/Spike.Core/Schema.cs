using Npgsql;

namespace Spike.Core;

public static class Schema
{
    // Tabela mínima do plano (etapa 0) + coluna de partição + tabelas de lease por partição e contador por chave.
    // O índice parcial do contrato cobre pending/claimed; o filtro de cabeça precisa enxergar 'dlq' também,
    // por isso um segundo índice parcial (achado do spike, ver RESULTADOS.md).
    public const string Ddl = """
        DROP TABLE IF EXISTS outbox, outbox_partitions, outbox_keys, outbox_instances;

        CREATE TABLE outbox (
            id           uuid        PRIMARY KEY,
            key          text        NOT NULL,
            partition    int         NOT NULL,
            sequence     bigint      NOT NULL,
            status       text        NOT NULL DEFAULT 'pending',
            lease_until  timestamptz NULL,
            owner        text        NULL,
            fence        bigint      NOT NULL DEFAULT 0,  -- token de fencing: sobe a cada claim
            attempts     int         NOT NULL DEFAULT 0,  -- tentativas consumidas (defeito); transporte não consome
            created_at   timestamptz NOT NULL DEFAULT clock_timestamp(),
            published_at timestamptz NULL,
            payload      bytea       NOT NULL
        );

        CREATE INDEX ix_outbox_claimable ON outbox (id)
            WHERE status IN ('pending', 'claimed');
        CREATE INDEX ix_outbox_head ON outbox (key, sequence)
            WHERE status IN ('pending', 'claimed', 'dlq');

        CREATE TABLE outbox_partitions (
            partition   int         PRIMARY KEY,
            owner       text        NULL,
            lease_until timestamptz NULL,
            epoch       bigint      NOT NULL DEFAULT 0
        );

        CREATE TABLE outbox_keys (
            key text   PRIMARY KEY,
            seq bigint NOT NULL
        );

        -- Membros vivos, para calcular a fatia justa de partições: ceil(P / instâncias vivas).
        CREATE TABLE outbox_instances (
            owner     text        PRIMARY KEY,
            heartbeat timestamptz NOT NULL
        );
        """;

    public static async Task ResetAsync(NpgsqlDataSource ds, int partitions, CancellationToken ct = default)
    {
        await using var cmd = ds.CreateCommand(Ddl);
        await cmd.ExecuteNonQueryAsync(ct);

        await using var seed = ds.CreateCommand(
            "INSERT INTO outbox_partitions (partition) SELECT generate_series(0, @p - 1)");
        seed.Parameters.AddWithValue("p", partitions);
        await seed.ExecuteNonQueryAsync(ct);
    }

    // FNV-1a 32 bits: estável entre processos (string.GetHashCode não é). O Kafka usa murmur2; aqui só importa estabilidade.
    public static int PartitionOf(string key, int partitions)
    {
        uint hash = 2166136261;
        foreach (var c in key)
        {
            hash ^= c;
            hash *= 16777619;
        }
        return (int)(hash % (uint)partitions);
    }
}
