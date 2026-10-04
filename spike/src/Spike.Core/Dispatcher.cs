using System.Collections.Concurrent;
using System.Data;
using System.Diagnostics;
using Npgsql;

namespace Spike.Core;

public enum ClaimMode
{
    /// <summary>Claim por linha em qualquer partição, sem lease de partição e sem ordem (decisão da v0.1).</summary>
    Rows,
    /// <summary>Lease por partição + claim por linha dentro das partições próprias, sem filtro de cabeça (candidato v0.2).</summary>
    Partitioned,
    /// <summary>Lease por partição + filtro de cabeça por chave com profundidade M (candidato v0.2).</summary>
    PartitionedOrdered,
    /// <summary>Filtro de cabeça por chave sem lease de partição ("cabeça por chave sozinha").</summary>
    HeadOnly,
}

public sealed record DispatcherOptions(
    string Owner,
    ClaimMode Mode,
    int Partitions,
    int BatchSize = 100,
    TimeSpan? Lease = null,
    int HeadDepth = 1)
{
    public TimeSpan LeaseOrDefault => Lease ?? TimeSpan.FromSeconds(30);
    public bool UsesPartitionLease => Mode is ClaimMode.Partitioned or ClaimMode.PartitionedOrdered;
    public bool UsesHeadFilter => Mode is ClaimMode.PartitionedOrdered or ClaimMode.HeadOnly;
}

/// <summary>
/// Fence: token de fencing, sobe a cada claim e nunca desce (único por reivindicação).
/// Separado de <c>attempts</c> na tabela, que conta só tentativas consumidas — falha de transporte não consome.
/// </summary>
public sealed record Claimed(Guid Id, string Key, long Sequence, long Fence, int Partition, DateTime ClaimedAt, DateTime LeaseUntil);

public sealed record Released(Guid Id, long Fence, DateTime At);

public sealed class Dispatcher(NpgsqlDataSource ds, DispatcherOptions options)
{
    public DispatcherOptions Options => options;
    public ConcurrentQueue<double> ClaimMillis { get; } = new();
    public ConcurrentQueue<Claimed> ClaimLog { get; } = new();
    public ConcurrentQueue<Released> ReleaseLog { get; } = new();
    public long Cycles;
    public long ClaimedTotal;
    public long MarkedTotal;
    public long ReleasedTotal;
    public long FencedTotal;

    public async Task<IReadOnlyList<Claimed>> ClaimAsync(CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        await using var conn = await ds.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        var rows = await ClaimInTransactionAsync(conn, tx, ct);
        await tx.CommitAsync(ct);

        // Ciclos sem partição também contam (revisão: excluí-los favorecia os modos particionados).
        ClaimMillis.Enqueue(sw.Elapsed.TotalMilliseconds);
        Interlocked.Increment(ref Cycles);
        Interlocked.Add(ref ClaimedTotal, rows.Count);
        foreach (var r in rows)
            ClaimLog.Enqueue(r);

        // RETURNING não garante ordem; a publicação precisa ser na ordem da chave.
        return [.. rows.OrderBy(r => r.Key, StringComparer.Ordinal).ThenBy(r => r.Sequence)];
    }

    /// <summary>Para a pergunta 2: reivindica e devolve a transação aberta, sem commit.</summary>
    public async Task<(NpgsqlConnection Conn, NpgsqlTransaction Tx, IReadOnlyList<Claimed> Rows)> ClaimUncommittedAsync(CancellationToken ct = default)
    {
        var conn = await ds.OpenConnectionAsync(ct);
        var tx = await conn.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        return (conn, tx, await ClaimInTransactionAsync(conn, tx, ct));
    }

    private async Task<List<Claimed>> ClaimInTransactionAsync(NpgsqlConnection conn, NpgsqlTransaction tx, CancellationToken ct)
    {
        int[]? partitions = null;
        if (options.UsesPartitionLease)
        {
            partitions = await AcquirePartitionsAsync(conn, tx, ct);
            if (partitions.Length == 0)
                return [];
        }
        return await ClaimRowsAsync(conn, tx, partitions, ct);
    }

    // Heartbeat + fatia justa = ceil(P / instâncias vivas). Devolve o excesso e pega até a fatia.
    // Candidato da v0.2: epoch é gravado mas não participa do fencing (revisão, achado 2).
    private async Task<int[]> AcquirePartitionsAsync(NpgsqlConnection conn, NpgsqlTransaction tx, CancellationToken ct)
    {
        // owner = @w repetido no UPDATE do excesso: o IN (CTE) não é reavaliado pelo EvalPlanQual,
        // e sem ele uma instância que voltou de pausa zeraria a partição que outra acabou de pegar (revisão, achado 1).
        await using var cmd = new NpgsqlCommand("""
            INSERT INTO outbox_instances (owner, heartbeat) VALUES (@w, clock_timestamp())
            ON CONFLICT (owner) DO UPDATE SET heartbeat = excluded.heartbeat;

            WITH share AS (
                SELECT ceil(@total::numeric / greatest(count(*), 1))::int AS n
                FROM outbox_instances WHERE heartbeat > clock_timestamp() - @lease
            ), excess AS (
                SELECT partition FROM outbox_partitions
                WHERE owner = @w
                ORDER BY partition
                OFFSET (SELECT n FROM share)
            )
            UPDATE outbox_partitions SET owner = NULL, lease_until = NULL
            WHERE owner = @w AND partition IN (SELECT partition FROM excess);

            UPDATE outbox_partitions p
            SET owner = @w,
                lease_until = clock_timestamp() + @lease,
                epoch = CASE WHEN p.owner IS NOT DISTINCT FROM @w THEN p.epoch ELSE p.epoch + 1 END
            WHERE p.partition IN (
                SELECT partition FROM outbox_partitions
                WHERE owner = @w OR owner IS NULL OR lease_until < clock_timestamp()
                ORDER BY (owner IS NOT DISTINCT FROM @w) DESC, partition
                LIMIT (SELECT ceil(@total::numeric / greatest(count(*), 1))::int
                       FROM outbox_instances WHERE heartbeat > clock_timestamp() - @lease)
                FOR UPDATE SKIP LOCKED)
              AND (p.owner = @w OR p.owner IS NULL OR p.lease_until < clock_timestamp())
            RETURNING p.partition;
            """, conn, tx);
        cmd.Parameters.AddWithValue("w", options.Owner);
        cmd.Parameters.AddWithValue("total", options.Partitions);
        cmd.Parameters.AddWithValue("lease", options.LeaseOrDefault);

        var result = new List<int>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        do
        {
            while (await reader.ReadAsync(ct))
                result.Add(reader.GetInt32(0));
        } while (await reader.NextResultAsync(ct));
        return [.. result];
    }

    private async Task<List<Claimed>> ClaimRowsAsync(
        NpgsqlConnection conn, NpgsqlTransaction tx, int[]? partitions, CancellationToken ct)
    {
        var partitionFilter = partitions is null ? "" : "AND partition = ANY(@parts)";

        // FOR UPDATE não aceita DISTINCT nem window function no mesmo nível: a cabeça por chave
        // é calculada numa subconsulta em FROM e o lock fica só na tabela base (FOR UPDATE OF o).
        // Achado A1: ao travar uma linha que outra transação alterou e commitou, o PostgreSQL
        // reavalia (EvalPlanQual) só os predicados sobre a tabela travada — não a subconsulta.
        // Sem repetir a condição de "reivindicável" em o, a linha já reivindicada por outro passa de novo.
        var candidates = options.UsesHeadFilter
            ? $"""
              SELECT o.id FROM outbox o
              JOIN (
                  SELECT id, rn FROM (
                      SELECT id,
                             row_number() OVER w AS rn,
                             bool_or(status = 'dlq' OR (status = 'claimed' AND lease_until >= clock_timestamp())) OVER w AS blocked
                      FROM outbox
                      WHERE status IN ('pending', 'claimed', 'dlq') {partitionFilter}
                      WINDOW w AS (PARTITION BY key ORDER BY sequence ROWS BETWEEN UNBOUNDED PRECEDING AND CURRENT ROW)
                  ) h
                  WHERE rn <= @depth AND NOT blocked
              ) heads ON heads.id = o.id
              WHERE (o.status = 'pending' OR (o.status = 'claimed' AND o.lease_until < clock_timestamp()))
              ORDER BY heads.rn, o.id
              LIMIT @n
              FOR UPDATE OF o SKIP LOCKED
              """
            : $"""
              SELECT id FROM outbox
              WHERE status IN ('pending', 'claimed')
                AND (status = 'pending' OR lease_until < clock_timestamp())
                {partitionFilter}
              ORDER BY id
              LIMIT @n
              FOR UPDATE SKIP LOCKED
              """;

        // A condição de reivindicável também no UPDATE externo: defesa em profundidade contra o A1
        // caso o planner reexecute a subconsulta ou alguém acrescente um JOIN no nível errado.
        await using var cmd = new NpgsqlCommand($"""
            UPDATE outbox o
            SET status = 'claimed', owner = @w, fence = o.fence + 1,
                lease_until = clock_timestamp() + @lease
            WHERE o.id IN ({candidates})
              AND (o.status = 'pending' OR (o.status = 'claimed' AND o.lease_until < clock_timestamp()))
            RETURNING o.id, o.key, o.sequence, o.fence, o.partition, clock_timestamp(), o.lease_until
            """, conn, tx);
        cmd.Parameters.AddWithValue("w", options.Owner);
        cmd.Parameters.AddWithValue("lease", options.LeaseOrDefault);
        cmd.Parameters.AddWithValue("n", options.BatchSize);
        cmd.Parameters.AddWithValue("depth", (long)options.HeadDepth);
        if (partitions is not null)
            cmd.Parameters.AddWithValue("parts", partitions);

        var rows = new List<Claimed>(options.BatchSize);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            rows.Add(new Claimed(reader.GetGuid(0), reader.GetString(1), reader.GetInt64(2), reader.GetInt64(3),
                reader.GetInt32(4), reader.GetDateTime(5), reader.GetDateTime(6)));
        return rows;
    }

    /// <summary>Marcação com fencing: só vale se a linha ainda está com este token (owner + fence).</summary>
    public async Task<int> MarkPublishedAsync(IReadOnlyList<Claimed> batch, CancellationToken ct = default)
    {
        var done = await FinishAsync(batch, "status = 'published', lease_until = NULL, published_at = clock_timestamp()", ct);
        Interlocked.Add(ref MarkedTotal, done.Count);
        Interlocked.Add(ref FencedTotal, batch.Count - done.Count);
        return done.Count;
    }

    /// <summary>Falha de transporte: devolve o lote sem esperar o lease e sem consumir tentativa (attempts intacto).</summary>
    public async Task<int> ReleaseAsync(IReadOnlyList<Claimed> batch, CancellationToken ct = default)
    {
        var done = await FinishAsync(batch, "status = 'pending', lease_until = NULL", ct);
        Interlocked.Add(ref ReleasedTotal, done.Count);
        foreach (var r in done)
            ReleaseLog.Enqueue(r);
        return done.Count;
    }

    private async Task<List<Released>> FinishAsync(IReadOnlyList<Claimed> batch, string set, CancellationToken ct)
    {
        if (batch.Count == 0) return [];
        await using var cmd = ds.CreateCommand($"""
            UPDATE outbox o SET {set}
            FROM unnest(@ids, @fences) AS m(id, fence)
            WHERE o.id = m.id AND o.fence = m.fence AND o.owner = @w AND o.status = 'claimed'
            RETURNING o.id, m.fence, clock_timestamp()
            """);
        cmd.Parameters.AddWithValue("ids", batch.Select(b => b.Id).ToArray());
        cmd.Parameters.AddWithValue("fences", batch.Select(b => b.Fence).ToArray());
        cmd.Parameters.AddWithValue("w", options.Owner);
        var done = new List<Released>(batch.Count);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            done.Add(new Released(reader.GetGuid(0), reader.GetInt64(1), reader.GetDateTime(2)));
        return done;
    }

    public async Task<int> RunOnceAsync(FakePublisher publisher, CancellationToken ct = default)
    {
        var batch = await ClaimAsync(ct);
        if (batch.Count == 0) return 0;
        try
        {
            await publisher.PublishAsync(options.Owner, batch, ct);
        }
        catch (FakeTransportException)
        {
            await ReleaseAsync(batch, CancellationToken.None);
            return 0;
        }
        return await MarkPublishedAsync(batch, CancellationToken.None);
    }

    public async Task RunAsync(FakePublisher publisher, TimeSpan idleDelay, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                if (await RunOnceAsync(publisher, ct) == 0)
                    await Task.Delay(idleDelay, ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
    }

    public async Task ReleasePartitionsAsync(CancellationToken ct = default)
    {
        await using var cmd = ds.CreateCommand("""
            UPDATE outbox_partitions SET owner = NULL, lease_until = NULL WHERE owner = @w;
            DELETE FROM outbox_instances WHERE owner = @w;
            """);
        cmd.Parameters.AddWithValue("w", options.Owner);
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
