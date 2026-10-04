using System.Diagnostics;
using System.Globalization;
using Spike.Core;

namespace Spike.Tests;

[Collection("pg")]
public sealed class Q1_Q3_ClaimLeaseFencing(PgFixture pg, ITestOutputHelper output)
{
    private const string File = "q1-q3.md";

    // Pergunta 1: N dispatchers reivindicam lotes disjuntos? (lease longo: nenhuma reentrega é legítima)
    [Theory]
    [InlineData(ClaimMode.Rows)]
    [InlineData(ClaimMode.Partitioned)]
    public async Task Q1_OitoDispatchers_ReivindicamLotesDisjuntos(ClaimMode mode)
    {
        const int partitions = 16, preloaded = 20_000, live = 5_000;
        await Schema.ResetAsync(pg.DataSource, partitions);
        await Producer.BulkInsertAsync(pg.DataSource, preloaded, keyCount: 1_000, partitions);

        var publisher = new FakePublisher { YieldBetweenMessages = true };

        // Produtores escrevendo enquanto os dispatchers drenam: linhas novas não podem escapar nem duplicar.
        async Task ProduceAsync(CancellationToken ct)
        {
            for (var i = 0; i < live; i++)
            {
                await using var conn = await pg.DataSource.OpenConnectionAsync(CancellationToken.None);
                await using var tx = await conn.BeginTransactionAsync(CancellationToken.None);
                var key = $"live{i % 200}";
                var seq = await Producer.NextSequenceAsync(conn, tx, key);
                await Producer.InsertAsync(conn, tx, key, seq, partitions);
                await tx.CommitAsync(CancellationToken.None);
            }
        }

        var (elapsed, dispatchers, completed) = await Run.UntilDrainedAsync(
            pg, 8, i => new DispatcherOptions($"w{i}", mode, partitions, BatchSize: 100),
            publisher, preloaded + live, TimeSpan.FromMinutes(2), ProduceAsync);

        var log = publisher.Log.ToArray();
        var publishedTwice = log.GroupBy(p => p.Id).Count(g => g.Count() > 1);
        var reclaimed = log.Count(p => p.Fence > 1);
        var perInstance = dispatchers.Select(d => d.ClaimedTotal).ToArray();

        Report.Append(output, File,
            $"| Q1 | {mode} | 8 dispatchers | {preloaded + live} msgs | {elapsed.TotalSeconds:F1}s | " +
            $"publicadas 2x: {publishedTwice} | fence>1: {reclaimed} | barradas: {dispatchers.Sum(d => d.FencedTotal)} | " +
            $"linhas por instância: {string.Join("/", perInstance)} |");

        Assert.True(completed, "não drenou no tempo");
        Assert.Equal(preloaded + live, log.Length);
        Assert.Equal(0, publishedTwice);
        Assert.Equal(0, reclaimed);
        // Sem isso, "0 duplicatas" poderia ser uma instância fazendo todo o trabalho (revisão, achado 8).
        Assert.All(perInstance, n => Assert.True(n > 0, "instância sem trabalho: não houve concorrência real"));
        Assert.Equal(0, await pg.ScalarAsync("SELECT count(*) FROM outbox WHERE status <> 'published'"));
    }

    // Pergunta 1 sob reclaim: lease curto, lotes lentos que estouram o lease e falhas de transporte no meio do lote.
    // Oráculo: por linha, cada claim só começa depois que o anterior terminou (lease vencido ou devolvido).
    [Fact]
    public async Task Q1b_ReclaimSobConcorrencia_NenhumaLinhaComDoisLeasesValidos()
    {
        const int partitions = 16, total = 10_000;
        var lease = TimeSpan.FromMilliseconds(300);
        await Schema.ResetAsync(pg.DataSource, partitions);
        await Producer.BulkInsertAsync(pg.DataSource, total, keyCount: 1_000, partitions);

        var publisher = new FakePublisher
        {
            // 15% dos lotes demoram 2x o lease: o lote volta para outro dispatcher e a marcação do lento é barrada.
            LatencyPerBatch = () => Random.Shared.NextDouble() < 0.15 ? lease * 2 : TimeSpan.Zero,
            FailureRate = 0.05,
            PartialFailure = true,
        };
        var (elapsed, dispatchers, completed) = await Run.UntilDrainedAsync(
            pg, 8, i => new DispatcherOptions($"w{i}", ClaimMode.Rows, partitions, BatchSize: 50, Lease: lease),
            publisher, total, TimeSpan.FromMinutes(2));

        var claims = dispatchers.SelectMany(d => d.ClaimLog).ToArray();
        var releases = dispatchers.SelectMany(d => d.ReleaseLog).ToDictionary(r => (r.Id, r.Fence), r => r.At);

        // Tolerância de 5 ms: o relógio do Docker/WSL2 recua até ~1,7 ms (teste Ambiente_*); um claim duplo real
        // sobrepõe na escala do lease (centenas de ms). A maior sobreposição observada é reportada.
        var overlaps = 0;
        var worstOverlapMs = 0.0;
        var fenceGaps = 0;
        foreach (var byRow in claims.GroupBy(c => c.Id))
        {
            var ordered = byRow.OrderBy(c => c.Fence).ToArray();
            for (var i = 1; i < ordered.Length; i++)
            {
                var prev = ordered[i - 1];
                if (ordered[i].Fence != prev.Fence + 1) fenceGaps++;
                var prevEnd = releases.TryGetValue((prev.Id, prev.Fence), out var releasedAt) && releasedAt < prev.LeaseUntil
                    ? releasedAt
                    : prev.LeaseUntil;
                var overlapMs = (prevEnd - ordered[i].ClaimedAt).TotalMilliseconds;
                worstOverlapMs = Math.Max(worstOverlapMs, overlapMs);
                if (overlapMs > 5) overlaps++;
            }
        }

        var log = publisher.Log.ToArray();
        var consumed = await pg.ScalarAsync("SELECT count(*) FROM outbox WHERE attempts > 0");
        var unpublished = await pg.ScalarAsync("SELECT count(*) FROM outbox WHERE status <> 'published'");

        Report.Append(output, File,
            $"| Q1b | Rows, lease {lease.TotalMilliseconds} ms | {total} msgs em {elapsed.TotalSeconds:F1}s | claims: {claims.Length} | " +
            $"reivindicações após lease/devolução: {claims.Count(c => c.Fence > 1)} | marcações barradas: {dispatchers.Sum(d => d.FencedTotal)} | " +
            $"falhas de transporte: {publisher.Failures} (devolvidas {dispatchers.Sum(d => d.ReleasedTotal)}) | entregas duplicadas: {log.Length - log.Select(p => p.Id).Distinct().Count()} | " +
            $"sobreposições > 5 ms: {overlaps} (pior {worstOverlapMs:F1} ms) | buracos de fence: {fenceGaps} | attempts consumidos: {consumed} |");

        Assert.True(completed, "não drenou no tempo");
        Assert.Equal(0, unpublished);
        Assert.Equal(total, log.Select(p => p.Id).Distinct().Count()); // pelo menos uma vez
        Assert.True(claims.Any(c => c.Fence > 1), "o cenário não produziu reclaim");
        Assert.True(dispatchers.Sum(d => d.FencedTotal) > 0, "o cenário não produziu marcação atrasada");
        Assert.True(publisher.Failures > 0, "o cenário não produziu falha de transporte");
        Assert.Equal(0, overlaps);
        Assert.Equal(0, fenceGaps);
        Assert.Equal(0, consumed); // falha de transporte e lease vencido não gastam tentativa
    }

    // Pergunta 2a: instância morta DURANTE A PUBLICAÇÃO (claim já commitado) libera o lote após o lease?
    [Theory]
    [InlineData(ClaimMode.Rows)]
    [InlineData(ClaimMode.Partitioned)]
    public async Task Q2_KillDuranteAPublicacao_LoteVoltaAposLease(ClaimMode mode)
    {
        const int partitions = 4, total = 500, batch = 200;
        var lease = TimeSpan.FromSeconds(5);
        await Schema.ResetAsync(pg.DataSource, partitions);
        await Producer.BulkInsertAsync(pg.DataSource, total, keyCount: 50, partitions);

        using var child = StartChild("victim", mode, partitions, batch, lease, "publishing");
        var line = await ReadUntilAsync(child, "CLAIMED", TimeSpan.FromSeconds(60));
        var claimedAt = Stopwatch.StartNew();
        child.Kill(entireProcessTree: true);
        await child.WaitForExitAsync();
        var victimRows = (int)await pg.ScalarAsync("SELECT count(*) FROM outbox WHERE owner = 'victim' AND status = 'claimed'");

        // Mesmo lease da vítima: a janela de vida das instâncias usa o lease de quem calcula
        // (com 30 s aqui, a vítima morta conta como viva e o sobrevivente só pega metade das partições).
        var survivor = new Dispatcher(pg.DataSource, new DispatcherOptions("survivor", mode, partitions, BatchSize: 1_000, Lease: lease));
        var publisher = new FakePublisher();

        // Antes do lease vencer: o lote da vítima não pode ser tocado.
        var early = await survivor.ClaimAsync();
        Assert.DoesNotContain(early, c => c.Fence > 1);
        await publisher.PublishAsync("survivor", early, default);
        await survivor.MarkPublishedAsync(early);

        // Depois: o lote volta, com fence 2. Acumula ciclos porque o lease da partição e o das
        // linhas vencem em instantes ligeiramente diferentes (o sobrevivente pode pegar a partição antes das linhas).
        var recovered = new List<Claimed>();
        var firstRetake = TimeSpan.Zero;
        while (claimedAt.Elapsed < lease + TimeSpan.FromSeconds(10) && recovered.Count(r => r.Fence == 2) < batch)
        {
            var cycle = await survivor.ClaimAsync();
            if (firstRetake == TimeSpan.Zero && cycle.Any(r => r.Fence == 2))
                firstRetake = claimedAt.Elapsed;
            await publisher.PublishAsync("survivor", cycle, default);
            await survivor.MarkPublishedAsync(cycle);
            recovered.AddRange(cycle);
            if (cycle.Count == 0)
                await Task.Delay(100);
        }
        var recoveredAfter = claimedAt.Elapsed;

        var retaken = recovered.Count(r => r.Fence == 2);
        Report.Append(output, File,
            $"| Q2a | {mode} | kill durante a publicação | {line} | lote da vítima: {victimRows} | antes do lease: {early.Count} linhas (fence 1) | " +
            $"retomadas: {retaken} com fence 2 (de {recovered.Count} no ciclo); primeira em {firstRetake.TotalSeconds:F1}s, todas em {recoveredAfter.TotalSeconds:F1}s, lease {lease.TotalSeconds}s |");

        Assert.Equal(batch, victimRows);
        Assert.Equal(batch, retaken);
        Assert.Equal(total, early.Count + recovered.Count);
        // Limite inferior: o lote não pode voltar antes do lease (o laço só media o teto — revisão, achado 10).
        Assert.True(firstRetake >= lease - TimeSpan.FromMilliseconds(500), $"voltou cedo demais: {firstRetake}");
        Assert.True(recoveredAfter < lease + TimeSpan.FromSeconds(3));
        Assert.Equal(0, await pg.ScalarAsync("SELECT count(*) FROM outbox WHERE status <> 'published'"));
    }

    // Pergunta 2b: instância morta COM A TRANSAÇÃO DE CLAIM ABERTA (UPDATE feito, sem commit).
    // O backend precisa perceber a conexão caída e abortar; até lá as linhas ficam travadas (SKIP LOCKED pula).
    [Fact]
    public async Task Q2b_KillComClaimAberto_LinhasVoltamQuandoOBackendAborta()
    {
        const int partitions = 4, total = 500, batch = 200;
        var lease = TimeSpan.FromSeconds(5);
        await Schema.ResetAsync(pg.DataSource, partitions);
        await Producer.BulkInsertAsync(pg.DataSource, total, keyCount: 50, partitions);

        using var child = StartChild("victim", ClaimMode.Rows, partitions, batch, lease, "open-claim");
        var line = await ReadUntilAsync(child, "CLAIMED-OPEN", TimeSpan.FromSeconds(60));

        var survivor = new Dispatcher(pg.DataSource, new DispatcherOptions("survivor", ClaimMode.Rows, partitions, BatchSize: 1_000, Lease: lease));
        var whileAlive = await survivor.ClaimAsync(); // vítima viva: as 200 travadas são puladas
        await survivor.MarkPublishedAsync(whileAlive);

        var killedAt = Stopwatch.StartNew();
        child.Kill(entireProcessTree: true);
        await child.WaitForExitAsync();

        var recovered = new List<Claimed>();
        while (killedAt.Elapsed < TimeSpan.FromSeconds(60) && recovered.Count < batch)
        {
            var cycle = await survivor.ClaimAsync();
            await survivor.MarkPublishedAsync(cycle);
            recovered.AddRange(cycle);
            if (cycle.Count == 0)
                await Task.Delay(50);
        }
        var recoveredAfter = killedAt.Elapsed;
        var idleInTx = await pg.ScalarAsync("SELECT count(*) FROM pg_stat_activity WHERE state LIKE 'idle in transaction%'");

        Report.Append(output, File,
            $"| Q2b | Rows | kill com claim aberto | {line} | com a vítima viva o sobrevivente pegou {whileAlive.Count} (pulou as travadas) | " +
            $"recuperadas: {recovered.Count} em {recoveredAfter.TotalSeconds:F2}s após o kill, fence {string.Join(",", recovered.Select(r => r.Fence).Distinct())} (o UPDATE da vítima foi desfeito) | " +
            $"backends idle in transaction no fim: {idleInTx} |");

        Assert.Equal(batch, int.Parse(line.Split(' ')[1], CultureInfo.InvariantCulture));
        Assert.Equal(total - batch, whileAlive.Count);
        Assert.Equal(batch, recovered.Count);
        Assert.All(recovered, r => Assert.Equal(1, r.Fence));
    }

    // Pergunta 3: a marcação atrasada do dono antigo é barrada? (donos diferentes: barra pelo owner)
    [Theory]
    [InlineData(ClaimMode.Rows)]
    [InlineData(ClaimMode.Partitioned)]
    public async Task Q3_MarcacaoAtrasada_BarradaPeloFencing(ClaimMode mode)
    {
        const int partitions = 1, total = 50;
        await Schema.ResetAsync(pg.DataSource, partitions);
        await Producer.BulkInsertAsync(pg.DataSource, total, keyCount: 5, partitions);

        var w1 = new Dispatcher(pg.DataSource, new DispatcherOptions("w1", mode, partitions));
        var w2 = new Dispatcher(pg.DataSource, new DispatcherOptions("w2", mode, partitions));

        var first = await w1.ClaimAsync();
        await ExpireLeasesAsync();
        var second = await w2.ClaimAsync();
        var lateMark = await w1.MarkPublishedAsync(first);
        var ownMark = await w2.MarkPublishedAsync(second);

        // Caso vizinho: lease vencido mas ninguém reivindicou — a marcação do dono antigo passa.
        await Schema.ResetAsync(pg.DataSource, partitions);
        await Producer.BulkInsertAsync(pg.DataSource, total, keyCount: 5, partitions);
        var lonely = await w1.ClaimAsync();
        await ExpireLeasesAsync();
        var lonelyMark = await w1.MarkPublishedAsync(lonely);

        Report.Append(output, File,
            $"| Q3 | {mode} | w1 reivindicou {first.Count} | w2 reivindicou {second.Count} (fence {second.Max(s => s.Fence)}) | " +
            $"marcação atrasada de w1: {lateMark} linhas | marcação de w2: {ownMark} | lease vencido sem novo dono: w1 marca {lonelyMark} |");

        Assert.Equal(total, first.Count);
        Assert.Equal(total, second.Count);
        Assert.Equal(0, lateMark);
        Assert.Equal(total, ownMark);
        Assert.Equal(total, lonelyMark);
    }

    // Pergunta 3 com o MESMO owner: só o fence separa o ciclo antigo do novo (caso mais comum em produção:
    // uma instância que reivindica de novo a própria linha depois do lease, ou dois processos com o mesmo nome).
    [Fact]
    public async Task Q3c_MesmoOwner_MarcacaoAtrasadaBarradaSoPeloFence()
    {
        const int partitions = 1, total = 50;
        await Schema.ResetAsync(pg.DataSource, partitions);
        await Producer.BulkInsertAsync(pg.DataSource, total, keyCount: 5, partitions);

        var oldIncarnation = new Dispatcher(pg.DataSource, new DispatcherOptions("pod-a", ClaimMode.Rows, partitions));
        var newIncarnation = new Dispatcher(pg.DataSource, new DispatcherOptions("pod-a", ClaimMode.Rows, partitions));

        var first = await oldIncarnation.ClaimAsync();
        await ExpireLeasesAsync();
        var second = await newIncarnation.ClaimAsync();
        var lateMark = await oldIncarnation.MarkPublishedAsync(first);
        var lateRelease = await oldIncarnation.ReleaseAsync(first);
        var ownMark = await newIncarnation.MarkPublishedAsync(second);

        Report.Append(output, File,
            $"| Q3c | Rows, mesmo owner | ciclo antigo fence {first.Max(f => f.Fence)}, novo fence {second.Max(s => s.Fence)} | " +
            $"marcação atrasada: {lateMark} | devolução atrasada: {lateRelease} | marcação do ciclo novo: {ownMark} |");

        Assert.All(first, f => Assert.Equal(1, f.Fence));
        Assert.All(second, s => Assert.Equal(2, s.Fence));
        Assert.Equal(0, lateMark);
        Assert.Equal(0, lateRelease);
        Assert.Equal(total, ownMark);
    }

    // Consequência de Q3 para a G4: o fencing protege a marcação, não a publicação.
    // Prova o modelo (o publicador falso aceita tudo), não o RabbitMQ — que fica para a etapa 3.
    [Fact]
    public async Task Q3b_FencingNaoProtegePublicacao_RegressaoChegaAoBroker()
    {
        const int partitions = 1;
        await Schema.ResetAsync(pg.DataSource, partitions);
        await Producer.BulkInsertAsync(pg.DataSource, 2, keyCount: 1, partitions);

        var publisher = new FakePublisher();
        var w1 = new Dispatcher(pg.DataSource, new DispatcherOptions("w1", ClaimMode.PartitionedOrdered, partitions, BatchSize: 1));
        var w2 = new Dispatcher(pg.DataSource, new DispatcherOptions("w2", ClaimMode.PartitionedOrdered, partitions, BatchSize: 1));

        var stalled = await w1.ClaimAsync();          // w1 pega seq 1 e "trava" (GC, rede) antes de publicar
        await ExpireLeasesAsync();
        Assert.Equal(1, await w2.RunOnceAsync(publisher)); // w2 reivindica seq 1 de novo, publica e marca
        Assert.Equal(1, await w2.RunOnceAsync(publisher)); // seq 2 vira cabeça e é publicada
        await publisher.PublishAsync("w1", stalled, default); // w1 acorda e publica seq 1 atrasada
        var mark = await w1.MarkPublishedAsync(stalled);

        var order = string.Join(" → ", publisher.Log.OrderBy(p => p.Order).Select(p => $"seq{p.Sequence}({p.Owner})"));
        Report.Append(output, File, $"| Q3b | PartitionedOrdered | broker recebeu: {order} | marcação de w1: {mark} |");

        Assert.Equal(0, mark);
        Assert.Equal([1L, 2L, 1L], publisher.Log.OrderBy(p => p.Order).Select(p => p.Sequence));
    }

    // Ambiente: o relógio de parede do container recua? (sustenta a tolerância de 5 ms dos oráculos por timestamp)
    [Fact]
    public async Task Ambiente_RelogioDoContainer_Monotonicidade()
    {
        await using var cmd = pg.DataSource.CreateCommand("""
            CREATE OR REPLACE FUNCTION pg_temp.probe(seconds int) RETURNS text LANGUAGE plpgsql AS $$
            DECLARE prev timestamptz := clock_timestamp(); cur timestamptz; n bigint := 0; back int := 0;
                    worst interval := '0'; stop timestamptz := clock_timestamp() + make_interval(secs => seconds);
            BEGIN
              LOOP
                cur := clock_timestamp(); n := n + 1;
                IF cur < prev THEN back := back + 1; worst := greatest(worst, prev - cur); END IF;
                prev := cur;
                EXIT WHEN cur > stop;
              END LOOP;
              RETURN format('amostras=%s recuos=%s maior_recuo=%s', n, back, worst);
            END $$;
            SELECT pg_temp.probe(30);
            """);
        cmd.CommandTimeout = 120;
        var result = (string)(await cmd.ExecuteScalarAsync())!;
        Report.Append(output, "ambiente.md", $"| relógio do container, 30 s | {result} |");
    }

    private Task ExpireLeasesAsync() => pg.ExecAsync("""
        UPDATE outbox SET lease_until = clock_timestamp() - interval '1 second' WHERE status = 'claimed';
        UPDATE outbox_partitions SET lease_until = clock_timestamp() - interval '1 second' WHERE owner IS NOT NULL;
        UPDATE outbox_instances SET heartbeat = clock_timestamp() - interval '1 hour';
        """);

    private Process StartChild(string owner, ClaimMode mode, int partitions, int batch, TimeSpan lease, string window)
    {
        var dll = Path.Combine(AppContext.BaseDirectory, "Spike.Dispatcher.dll");
        var psi = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var arg in new[] { dll, pg.ConnectionString, owner, mode.ToString(), partitions.ToString(CultureInfo.InvariantCulture),
                     batch.ToString(CultureInfo.InvariantCulture), lease.TotalSeconds.ToString(CultureInfo.InvariantCulture), window })
            psi.ArgumentList.Add(arg);
        return Process.Start(psi)!;
    }

    private static async Task<string> ReadUntilAsync(Process p, string prefix, TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        while (true)
        {
            var line = await p.StandardOutput.ReadLineAsync(cts.Token)
                ?? throw new InvalidOperationException($"filho saiu: {await p.StandardError.ReadToEndAsync()}");
            if (line.StartsWith(prefix, StringComparison.Ordinal))
                return line;
        }
    }
}
