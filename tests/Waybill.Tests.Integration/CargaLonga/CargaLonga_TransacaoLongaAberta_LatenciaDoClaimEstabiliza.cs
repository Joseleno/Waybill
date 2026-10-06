using System.Diagnostics;
using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Waybill.EntityFrameworkCore;
using Waybill.EntityFrameworkCore.Dispatching;
using Waybill.Tests.Integration.Retencao;

namespace Waybill.Tests.Integration.CargaLonga;

// The trap of stage 5 (docs/plano.md): every claim changes `status`, a column of the partial index's predicate, so no
// update is HOT and each message leaves dead index entries behind. A long transaction pins the vacuum horizon and the
// claim slows down while the table looks stable. This scenario holds one open for 40% of the run under constant load,
// closes it, and requires the claim latency to come back once autovacuum has run; it measures time, not only bytes.
// Autovacuum does not shrink the claim index, and the bloated index slows the claim in bursts for hours: once it has
// run, the scenario rebuilds the index, as OPERATIONS.md tells operators to (WAYBILL_LONG_REINDEX=0 skips it, to see
// the bursts).
// Duration: WAYBILL_LONG_DURATION (default 05:00:00, the scheduled job); rate: WAYBILL_LONG_RATE messages/s (200).
// A CSV with one line per sample goes to WAYBILL_LONG_OUTPUT (default carga-longa.csv next to the test assembly).
[Collection(PostgresCollection.Name)]
public sealed class CargaLonga_TransacaoLongaAberta_LatenciaDoClaimEstabiliza(PostgresFixture postgres)
{
    [Fact]
    [Trait("Category", "Long")]
    public async Task CargaLonga_DepoisQueATransacaoLongaFecha_LatenciaDoClaimVoltaETamanhoParaDeCrescer()
    {
        var ct = TestContext.Current.CancellationToken;
        var duration = Setting("WAYBILL_LONG_DURATION", TimeSpan.Parse, TimeSpan.FromHours(5));
        var rate = Setting("WAYBILL_LONG_RATE", int.Parse, 200);
        var output = Setting("WAYBILL_LONG_OUTPUT", s => s, Path.Combine(AppContext.BaseDirectory, "carga-longa.csv"));
        var plan = Plan.For(duration);
        // The procedure OPERATIONS.md documents after a long transaction: rebuild the claim index once autovacuum has run.
        var reindexAfterRecovery = Setting("WAYBILL_LONG_REINDEX", s => s != "0", true);
        var reindexed = false;
        var database = await TestDatabase.CreateAsync(postgres);
        await using var dataSource = NpgsqlDataSource.Create(database.ConnectionString);
        // The autovacuum settings OPERATIONS.md recommends for the outbox.
        await ExecuteAsync(dataSource, "ALTER TABLE waybill.outbox SET (autovacuum_vacuum_scale_factor = 0.01, autovacuum_vacuum_threshold = 1000)", ct);

        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var claims = new ClaimLog();
        Task[] running =
        [
            ProduceAsync(database, rate, stop.Token),
            DispatchAsync(dataSource, claims, stop.Token),
            CleanAsync(dataSource, stop.Token),
        ];
        var workers = Task.WhenAll(running);
        var samples = new List<Sample>();
        await using var csv = new StreamWriter(output) { AutoFlush = true };
        await csv.WriteLineAsync("elapsed_s,phase,claims,claim_p95_ms,outbox_bytes,dead_tuples,rows,last_autovacuum,claim_p50_ms,claim_max_ms,claimable_index_bytes,pk_bytes,claimable_idx_scan,claimable_idx_tup_read,autovacuum_count,vacuum_running,claimable_idx_blks,pk_idx_blks,heap_blks");

        var started = Stopwatch.StartNew();
        NpgsqlConnection? holder = null;
        NpgsqlTransaction? longTransaction = null;
        DateTimeOffset? closedAt = null;
        try
        {
            while (started.Elapsed < duration)
            {
                await Task.Delay(plan.SampleEvery, ct);
                if (running.FirstOrDefault(t => t.IsFaulted) is { } dead)
                    await dead; // a worker died: fail now with its exception instead of measuring nothing
                var phase = plan.PhaseAt(started.Elapsed);
                if (phase == Phase.LongTransaction && holder is null)
                {
                    // REPEATABLE READ plus a query takes a snapshot that pins the vacuum horizon until commit.
                    holder = await dataSource.OpenConnectionAsync(ct);
                    longTransaction = await holder.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead, ct);
                    await using var pin = new NpgsqlCommand("SELECT count(*) FROM waybill.outbox", holder, longTransaction);
                    await pin.ExecuteScalarAsync(ct);
                }
                else if (phase == Phase.Recovery && holder is not null)
                {
                    await longTransaction!.CommitAsync(ct);
                    await holder.DisposeAsync();
                    holder = null;
                    closedAt = DateTimeOffset.UtcNow;
                }

                var sample = await SampleAsync(dataSource, started.Elapsed, phase, claims.TakeWindow(), ct);
                samples.Add(sample);
                await csv.WriteLineAsync(sample.ToCsv());

                if (reindexAfterRecovery && !reindexed && sample.LastAutovacuum > closedAt)
                {
                    await ExecuteAsync(dataSource, "REINDEX INDEX CONCURRENTLY waybill.ix_outbox_claimable", ct);
                    reindexed = true;
                    Console.WriteLine($"reindexed ix_outbox_claimable at {started.Elapsed}");
                }
            }
        }
        finally
        {
            stop.Cancel();
            if (holder is not null)
                await holder.DisposeAsync();
            try
            {
                await workers;
            }
            catch (OperationCanceledException)
            {
            }
        }

        Assert.NotNull(closedAt);
        var baseline = samples.Where(s => s.Phase == Phase.Baseline).ToList();

        // The trap was actually exercised: with the horizon pinned, dead tuples piled up well past anything the
        // baseline saw. Without this, a run where the snapshot did not hold anything would pass without proving a thing.
        var baselineDead = baseline.Max(s => s.DeadTuples);
        var pinnedDead = samples.Where(s => s.Phase == Phase.LongTransaction).Max(s => s.DeadTuples);
        Assert.True(pinnedDead > 2 * baselineDead,
            $"the long transaction did not pin the vacuum horizon: {pinnedDead} dead tuples at most with it open, {baselineDead} in the baseline; see {output}");

        // The final window counts only once autovacuum has run after the transaction closed.
        var vacuumed = samples.FirstOrDefault(s => s.LastAutovacuum > closedAt);
        Assert.True(vacuumed is not null, $"no autovacuum of waybill.outbox after the long transaction closed at {closedAt:O}; see {output}");
        var window = samples.Where(s => s.Elapsed >= vacuumed.Elapsed && s.Elapsed >= duration - plan.FinalWindow).ToList();
        Assert.True(window.Count >= 3, $"autovacuum ran too late ({vacuumed.Elapsed}) to leave a final window to measure; see {output}");
        Assert.True(reindexed || !reindexAfterRecovery, $"the claim index was never rebuilt; see {output}");
        var baselineP95 = Percentile95(baseline.SelectMany(s => s.ClaimMs));
        var finalP95 = Percentile95(window.SelectMany(s => s.ClaimMs));
        var allowed = Math.Max(2 * baselineP95, baselineP95 + 5);
        Assert.True(finalP95 <= allowed,
            $"claim p95 {finalP95:0.0} ms in the final window, baseline {baselineP95:0.0} ms (allowed {allowed:0.0} ms); see {output}");
        var growth = (double)window[^1].OutboxBytes / window[0].OutboxBytes;
        Assert.True(growth <= 1.05, $"waybill.outbox grew {growth:P1} within the final window; see {output}");
    }

    private enum Phase
    {
        Warmup,
        Baseline,
        LongTransaction,
        Recovery,
    }

    private sealed record Plan(TimeSpan Duration, TimeSpan SampleEvery, TimeSpan FinalWindow)
    {
        public static Plan For(TimeSpan duration) => new(
            duration,
            SampleEvery: TimeSpan.FromTicks(Math.Min(TimeSpan.FromMinutes(1).Ticks, duration.Ticks / 60)),
            FinalWindow: TimeSpan.FromTicks(Math.Min(TimeSpan.FromMinutes(15).Ticks, (long)(duration.Ticks * 0.35 / 2))));

        // 10% warm-up, 15% baseline, 40% with the long transaction open, 35% recovery.
        public Phase PhaseAt(TimeSpan elapsed) => (elapsed / Duration) switch
        {
            < 0.10 => Phase.Warmup,
            < 0.25 => Phase.Baseline,
            < 0.65 => Phase.LongTransaction,
            _ => Phase.Recovery,
        };
    }

    private sealed record Sample(
        TimeSpan Elapsed, Phase Phase, IReadOnlyList<double> ClaimMs, long OutboxBytes, long DeadTuples, long Rows, DateTimeOffset? LastAutovacuum,
        string Diagnostics)
    {
        public string ToCsv() => string.Join(',',
            ((long)Elapsed.TotalSeconds).ToString(CultureInfo.InvariantCulture),
            Phase,
            ClaimMs.Count.ToString(CultureInfo.InvariantCulture),
            Percentile95(ClaimMs).ToString("0.00", CultureInfo.InvariantCulture),
            OutboxBytes.ToString(CultureInfo.InvariantCulture),
            DeadTuples.ToString(CultureInfo.InvariantCulture),
            Rows.ToString(CultureInfo.InvariantCulture),
            LastAutovacuum?.ToString("O", CultureInfo.InvariantCulture) ?? "",
            Percentile(ClaimMs, 0.50).ToString("0.00", CultureInfo.InvariantCulture),
            (ClaimMs.Count == 0 ? 0 : ClaimMs.Max()).ToString("0.00", CultureInfo.InvariantCulture),
            Diagnostics);
    }

    /// <summary>Claim durations since the last sample, recorded by the dispatch loop.</summary>
    private sealed class ClaimLog
    {
        private readonly Lock _lock = new();
        private List<double> _window = [];

        public void Add(double milliseconds)
        {
            lock (_lock)
                _window.Add(milliseconds);
        }

        public IReadOnlyList<double> TakeWindow()
        {
            lock (_lock)
            {
                var taken = _window;
                _window = [];
                return taken;
            }
        }
    }

    // Constant load through the real outbox: rate/10 messages every 100 ms, one transaction each.
    private static async Task ProduceAsync(TestDatabase database, int rate, CancellationToken ct)
    {
        await using var services = database.Services();
        var perTick = Math.Max(1, rate / 10);
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(100));
        while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
        {
            await using var scope = services.CreateAsyncScope();
            var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var outbox = scope.ServiceProvider.GetRequiredService<IOutbox<AppDbContext>>();
            for (var i = 0; i < perTick; i++)
                outbox.Enqueue(new InvoicePaid(Guid.NewGuid(), i), key: $"invoice-{i % 50}");
            await context.SaveChangesAsync(ct);
        }
    }

    // The dispatcher's own SQL (claim, then mark published), with the claim timed on its own.
    private static async Task DispatchAsync(NpgsqlDataSource dataSource, ClaimLog claims, CancellationToken ct)
    {
        var store = new OutboxStore(dataSource);
        const string owner = "long-load";
        while (!ct.IsCancellationRequested)
        {
            var watch = Stopwatch.StartNew();
            var claimed = await store.ClaimAsync(owner, 100, TimeSpan.FromSeconds(30), ct);
            claims.Add(watch.Elapsed.TotalMilliseconds);
            if (claimed.Count == 0)
            {
                await Task.Delay(50, ct);
                continue;
            }
            await store.FinishAsync(owner, claimed.Select(c => (c, PublishResult.Confirmed)).ToList(), 5, ct);
        }
    }

    // Retention on, as in production, so the table does not grow without bound.
    private static async Task CleanAsync(NpgsqlDataSource dataSource, CancellationToken ct)
    {
        var cleaner = RetentionHarness.Create(dataSource, new() { ConnectionString = "unused", OutboxRetention = TimeSpan.FromMinutes(2) });
        while (!ct.IsCancellationRequested)
        {
            await cleaner.RunOnceAsync(ct);
            await Task.Delay(TimeSpan.FromSeconds(30), ct);
        }
    }

    private static async Task<Sample> SampleAsync(NpgsqlDataSource dataSource, TimeSpan elapsed, Phase phase, IReadOnlyList<double> claims, CancellationToken ct)
    {
        await using var command = dataSource.CreateCommand("""
            SELECT pg_total_relation_size('waybill.outbox'), t.n_dead_tup, t.n_live_tup, t.last_autovacuum,
                   pg_relation_size('waybill.ix_outbox_claimable'), pg_relation_size('waybill.pk_outbox'),
                   i.idx_scan, i.idx_tup_read, t.autovacuum_count,
                   (SELECT count(*) FROM pg_stat_progress_vacuum p WHERE p.relid = t.relid),
                   ci.idx_blks_hit + ci.idx_blks_read, pk.idx_blks_hit + pk.idx_blks_read,
                   h.heap_blks_hit + h.heap_blks_read
            FROM pg_stat_user_tables t
            JOIN pg_stat_user_indexes i ON i.relid = t.relid AND i.indexrelname = 'ix_outbox_claimable'
            JOIN pg_statio_user_indexes ci ON ci.relid = t.relid AND ci.indexrelname = 'ix_outbox_claimable'
            JOIN pg_statio_user_indexes pk ON pk.relid = t.relid AND pk.indexrelname = 'pk_outbox'
            JOIN pg_statio_user_tables h ON h.relid = t.relid
            WHERE t.schemaname = 'waybill' AND t.relname = 'outbox'
            """);
        await using var reader = await command.ExecuteReaderAsync(ct);
        await reader.ReadAsync(ct);
        DateTimeOffset? lastAutovacuum = reader.IsDBNull(3) ? null : new DateTimeOffset(reader.GetDateTime(3), TimeSpan.Zero);
        var diagnostics = string.Join(',', Enumerable.Range(4, 9).Select(c => Convert.ToInt64(reader.GetValue(c), CultureInfo.InvariantCulture)));
        return new Sample(elapsed, phase, claims, reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2), lastAutovacuum, diagnostics);
    }

    private static async Task ExecuteAsync(NpgsqlDataSource dataSource, string sql, CancellationToken ct)
    {
        await using var command = dataSource.CreateCommand(sql);
        await command.ExecuteNonQueryAsync(ct);
    }

    private static double Percentile95(IEnumerable<double> values) => Percentile(values, 0.95);

    private static double Percentile(IEnumerable<double> values, double rank)
    {
        var sorted = values.Order().ToArray();
        return sorted.Length == 0 ? 0 : sorted[(int)Math.Ceiling(rank * sorted.Length) - 1];
    }

    private static T Setting<T>(string name, Func<string, T> parse, T fallback) =>
        Environment.GetEnvironmentVariable(name) is { Length: > 0 } value ? parse(value) : fallback;
}
