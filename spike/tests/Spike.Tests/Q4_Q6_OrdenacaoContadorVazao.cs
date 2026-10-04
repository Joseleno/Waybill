using System.Collections.Concurrent;
using System.Diagnostics;
using Npgsql;
using Spike.Core;

namespace Spike.Tests;

[Collection("pg")]
public sealed class Q4_Q6_OrdenacaoContadorVazao(PgFixture pg, ITestOutputHelper output)
{
    // Pergunta 4: a cabeça por chave se sustenta com SKIP LOCKED e 8 instâncias concorrentes?
    // Invariante: por chave, a ordem de chegada ao "broker" segue a sequence.
    [Theory]
    [InlineData(ClaimMode.HeadOnly, 1)]
    [InlineData(ClaimMode.HeadOnly, 4)]
    [InlineData(ClaimMode.PartitionedOrdered, 1)]
    [InlineData(ClaimMode.PartitionedOrdered, 4)]
    public async Task Q4_CabecaPorChave_SobConcorrencia(ClaimMode mode, int depth)
    {
        const int partitions = 16, total = 10_000, keys = 50;
        await Schema.ResetAsync(pg.DataSource, partitions);
        await Producer.BulkInsertAsync(pg.DataSource, total, keys, partitions);

        var publisher = new FakePublisher { YieldBetweenMessages = true };
        var (elapsed, dispatchers, completed) = await Run.UntilDrainedAsync(
            pg, 8, i => new DispatcherOptions($"w{i}", mode, partitions, BatchSize: 50, HeadDepth: depth),
            publisher, total, TimeSpan.FromMinutes(2));

        var inversions = publisher.Log
            .GroupBy(p => p.Key)
            .Sum(g => g.OrderBy(p => p.Order).Zip(g.OrderBy(p => p.Order).Skip(1)).Count(pair => pair.Second.Sequence < pair.First.Sequence));

        Report.Append(output, "q4.md",
            $"| {mode} | M={depth} | {(completed ? "drenou" : "NÃO drenou")} em {elapsed.TotalSeconds:F1}s | " +
            $"inversões por chave no broker: {inversions} | duplicatas: {publisher.Log.Count - total} | ciclos: {dispatchers.Sum(d => d.Cycles)} |");

        Assert.True(completed);
        Assert.Equal(total, publisher.Log.Count); // lease de 30 s: nenhuma reentrega é legítima aqui
        if (mode == ClaimMode.PartitionedOrdered || depth == 1)
            Assert.Equal(0, inversions);
        else
            Assert.True(inversions > 0, "controle negativo: cabeça sozinha com M>1 deveria inverter (revisão, achado 8)");
    }

    // Pergunta 5: custo do contador por chave e deadlock com chaves ordenadas.
    [Theory]
    [InlineData("sem contador", 0, false)]
    [InlineData("contador, chaves distintas", 1, false)]
    [InlineData("contador, 1 chave quente", 1, false)]
    [InlineData("contador, 3 de 5 chaves, ordem aleatória", 3, false)]
    [InlineData("contador, 3 de 5 chaves, ordenadas", 3, true)]
    [InlineData("contador fundido no fim da tx, chaves distintas", 1, false, true)]
    [InlineData("contador fundido no fim da tx, 1 chave quente", 1, false, true)]
    public async Task Q5_ContadorPorChave_ContencaoEDeadlock(string scenario, int keysPerTx, bool sorted, bool fused = false)
    {
        const int workers = 32;
        var duration = TimeSpan.FromSeconds(8);
        await Schema.ResetAsync(pg.DataSource, 16);

        var latencies = new ConcurrentQueue<double>();
        long committed = 0, deadlocks = 0, rolledBack = 0, timeouts = 0;
        using var cts = new CancellationTokenSource(duration);

        string[] PickKeys()
        {
            if (keysPerTx == 0) return [];
            if (scenario.Contains("quente")) return ["hot"];
            if (keysPerTx == 1) return [$"k{Random.Shared.Next(100_000)}"];
            var picked = Enumerable.Range(0, 5).OrderBy(_ => Random.Shared.Next()).Take(keysPerTx).Select(i => $"m{i}");
            return sorted ? [.. picked.Order(StringComparer.Ordinal)] : [.. picked];
        }

        async Task WorkerAsync()
        {
            while (!cts.IsCancellationRequested)
            {
                var keys = PickKeys();
                var sw = Stopwatch.StartNew();
                await using var conn = await pg.DataSource.OpenConnectionAsync();
                await using var tx = await conn.BeginTransactionAsync();
                try
                {
                    if (fused)
                    {
                        await using (var work = new NpgsqlCommand("SELECT pg_sleep(0.002)", conn, tx))
                            await work.ExecuteNonQueryAsync();
                        foreach (var k in keys)
                            await Producer.InsertWithNextSequenceAsync(conn, tx, k, 16);
                        if (Random.Shared.Next(10) == 0)
                        {
                            await tx.RollbackAsync();
                            Interlocked.Increment(ref rolledBack);
                            continue;
                        }
                        await tx.CommitAsync();
                        Interlocked.Increment(ref committed);
                        latencies.Enqueue(sw.Elapsed.TotalMilliseconds);
                        continue;
                    }

                    var seqs = new List<(string Key, long Seq)>();
                    foreach (var k in keys)
                        seqs.Add((k, await Producer.NextSequenceAsync(conn, tx, k)));

                    // Trabalho de negócio dentro da transação (2 ms no servidor; Task.Delay no Windows tem ~15 ms de resolução).
                    await using (var work = new NpgsqlCommand("SELECT pg_sleep(0.002)", conn, tx))
                        await work.ExecuteNonQueryAsync();

                    foreach (var (k, s) in seqs)
                        await Producer.InsertAsync(conn, tx, k, s, 16);
                    if (keys.Length == 0)
                        await Producer.InsertAsync(conn, tx, $"n{Random.Shared.Next(100_000)}", 0, 16);

                    // 10% de rollback: o contador desfaz junto, então não pode sobrar buraco na sequence.
                    if (Random.Shared.Next(10) == 0)
                    {
                        await tx.RollbackAsync();
                        Interlocked.Increment(ref rolledBack);
                        continue;
                    }
                    await tx.CommitAsync();
                    Interlocked.Increment(ref committed);
                    latencies.Enqueue(sw.Elapsed.TotalMilliseconds);
                }
                catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.DeadlockDetected)
                {
                    Interlocked.Increment(ref deadlocks);
                }
                catch (NpgsqlException e) when (e.InnerException is TimeoutException)
                {
                    // Cadeias de lock passam do CommandTimeout (30 s) no cenário sem ordem de chaves.
                    Interlocked.Increment(ref timeouts);
                }
            }
        }

        var started = Stopwatch.StartNew();
        await Task.WhenAll(Enumerable.Range(0, workers).Select(_ => Task.Run(WorkerAsync)));
        var elapsed = started.Elapsed;

        // Sequência contígua 1..n por chave, igual ao contador, e na ordem de commit.
        var gaps = await pg.ScalarAsync("""
            SELECT count(*) FROM (
                SELECT key, count(*) AS n, max(sequence) AS mx, count(DISTINCT sequence) AS d
                FROM outbox WHERE sequence > 0 GROUP BY key
            ) s JOIN outbox_keys k USING (key)
            WHERE s.n <> s.mx OR s.d <> s.n OR k.seq <> s.mx
            """);
        var outOfCommitOrder = await pg.ScalarAsync("""
            SELECT count(*) FROM (
                SELECT pg_xact_commit_timestamp(xmin) AS ts,
                       lag(pg_xact_commit_timestamp(xmin)) OVER (PARTITION BY key ORDER BY sequence) AS prev_ts
                FROM outbox WHERE sequence > 0
            ) t WHERE ts < prev_ts
            """);
        // Tamanho do maior recuo (µs): distingue violação real de relógio não monotônico da VM.
        var maxBackwardMicros = await pg.ScalarAsync("""
            SELECT coalesce(max(extract(epoch FROM prev_ts - ts) * 1e6), 0)::bigint FROM (
                SELECT pg_xact_commit_timestamp(xmin) AS ts,
                       lag(pg_xact_commit_timestamp(xmin)) OVER (PARTITION BY key ORDER BY sequence) AS prev_ts
                FROM outbox WHERE sequence > 0
            ) t WHERE ts < prev_ts
            """);
        await using (var diag = pg.DataSource.CreateCommand("""
            SELECT key, sequence, prev_seq, ts, prev_ts FROM (
                SELECT key, sequence, lag(sequence) OVER k AS prev_seq,
                       pg_xact_commit_timestamp(xmin) AS ts, lag(pg_xact_commit_timestamp(xmin)) OVER k AS prev_ts
                FROM outbox WHERE sequence > 0
                WINDOW k AS (PARTITION BY key ORDER BY sequence)
            ) t WHERE ts < prev_ts LIMIT 5
            """))
        await using (var r = await diag.ExecuteReaderAsync())
            while (await r.ReadAsync())
                Report.Append(output, "q5-diag.md", $"| {scenario} | key {r.GetString(0)} | seq {r.GetInt64(2)} commit {r.GetDateTime(4):HH:mm:ss.ffffff} → seq {r.GetInt64(1)} commit {r.GetDateTime(3):HH:mm:ss.ffffff} |");

        Report.Append(output, "q5.md",
            $"| {scenario} | {committed / elapsed.TotalSeconds:F0} tx/s | p50 {Report.Percentile(latencies, 50):F1} ms | " +
            $"p99 {Report.Percentile(latencies, 99):F1} ms | deadlocks: {deadlocks} | timeouts: {timeouts} | rollbacks: {rolledBack} | " +
            $"chaves com buraco/duplicata: {gaps} | fora da ordem de commit: {outOfCommitOrder} (maior recuo {maxBackwardMicros} µs) |");

        Assert.Equal(0, gaps);
        // commit_ts vem do relógio de parede; no Docker/WSL2 ele recua até ~1,7 ms (medido: 2 recuos em 60 s).
        // Recuo pequeno é artefato de medição; violação real do protocolo de lock seria arbitrária.
        Assert.True(outOfCommitOrder == 0 || maxBackwardMicros < 5_000, $"recuo de {maxBackwardMicros} µs não se explica pelo relógio");
        if (sorted || keysPerTx <= 1)
            Assert.Equal(0, deadlocks);
    }

    // Pergunta 6: vazão com e sem ordenação — quanto custa trazer a ordenação para a v0.1.
    [Theory]
    [InlineData(ClaimMode.Rows, 1, 1_000, 0)]
    [InlineData(ClaimMode.Partitioned, 1, 1_000, 0)]
    [InlineData(ClaimMode.PartitionedOrdered, 1, 1_000, 0)]
    [InlineData(ClaimMode.PartitionedOrdered, 10, 1_000, 0)]
    [InlineData(ClaimMode.HeadOnly, 1, 1_000, 0)]
    [InlineData(ClaimMode.Partitioned, 1, 20, 0)]
    [InlineData(ClaimMode.PartitionedOrdered, 1, 20, 0)]
    [InlineData(ClaimMode.PartitionedOrdered, 10, 20, 0)]
    [InlineData(ClaimMode.Partitioned, 1, 1_000, 20)]
    [InlineData(ClaimMode.PartitionedOrdered, 10, 1_000, 20)]
    public async Task Q6_Vazao_ComESemOrdenacao(ClaimMode mode, int depth, int keys, int confirmMs)
    {
        const int partitions = 16, total = 50_000;
        await Schema.ResetAsync(pg.DataSource, partitions);
        await Producer.BulkInsertAsync(pg.DataSource, total, keys, partitions);

        var publisher = new FakePublisher { ConfirmLatency = TimeSpan.FromMilliseconds(confirmMs) };
        var (elapsed, dispatchers, completed) = await Run.UntilDrainedAsync(
            pg, 8, i => new DispatcherOptions($"w{i}", mode, partitions, BatchSize: 100, HeadDepth: depth),
            publisher, total, TimeSpan.FromSeconds(90));

        var marked = dispatchers.Sum(d => d.MarkedTotal);
        var cycles = dispatchers.Sum(d => d.Cycles);
        var claimMs = dispatchers.SelectMany(d => d.ClaimMillis).ToArray();

        Report.Append(output, "q6.md",
            $"| {mode} | {(mode is ClaimMode.PartitionedOrdered or ClaimMode.HeadOnly ? $"M={depth}" : "-")} | {keys} chaves | confirm {confirmMs} ms | " +
            $"{marked / elapsed.TotalSeconds:F0} msg/s | {(completed ? "" : $"parou em 90s com {marked}/{total} | ")}" +
            $"{(double)marked / Math.Max(cycles, 1):F1} msg/ciclo | claim p50 {Report.Percentile(claimMs, 50):F1} ms p99 {Report.Percentile(claimMs, 99):F1} ms |");

        Assert.True(marked > 0);
    }
}
