using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using NpgsqlTypes;
using Waybill.EntityFrameworkCore;
using Waybill.EntityFrameworkCore.Dispatching;

namespace Waybill.Tests.Integration.G4;

// The cost criterion of ordering off (SPEC, written before measuring): the p99 of the application's transaction and the
// claim's throughput stay within 5% of 0.1.0-alpha. The baseline is that release emulated in the same run: no numbering
// nor terminal-status trigger, no head or blocked-key index, no key-list interceptor, and the claim SQL of the tag. Rounds
// interleave the two and medians are compared, against the noise of a shared runner. Scheduled job only.
[Collection(PostgresCollection.Name)]
public sealed class G4_CustoDesligada_ContraV01(PostgresFixture postgres)
{
    private const int Rounds = 5;
    private const int Producers = 16;

    // src/Waybill.EntityFrameworkCore.PostgreSql/Dispatching/OutboxStore.cs at v0.1.0-alpha.
    private const string V01ClaimSql = """
        WITH candidates AS MATERIALIZED (
            SELECT id FROM waybill.outbox
            WHERE status IN ('pending', 'claimed')
              AND (status = 'pending' OR lease_until < clock_timestamp())
            ORDER BY id
            LIMIT $3
            FOR UPDATE SKIP LOCKED)
        UPDATE waybill.outbox o
        SET status = 'claimed', owner = $1, fence = o.fence + 1, lease_until = clock_timestamp() + $2
        FROM candidates c
        WHERE o.id = c.id
          AND (o.status = 'pending' OR (o.status = 'claimed' AND o.lease_until < clock_timestamp()))
        RETURNING o.id, o.type, o.key, o.payload, o.content_type, o.headers, o.created_at, o.fence
        """;

    private static TimeSpan Window => TimeSpan.FromSeconds(int.TryParse(Environment.GetEnvironmentVariable("WAYBILL_COST_SECONDS"), out var s) ? s : 10);

    [Fact]
    [Trait("Category", "Long")]
    public async Task G4_CustoDesligada_TransacaoEClaimAteCincoPorCentoDaV01()
    {
        var (baseP99, offP99, baseRate, offRate) = (new List<double>(), new List<double>(), new List<double>(), new List<double>());
        for (var round = 0; round < Rounds; round++)
        {
            foreach (var baseline in round % 2 == 0 ? new[] { true, false } : [false, true])
            {
                var database = await TestDatabase.CreateAsync(postgres);
                if (baseline)
                {
                    await database.ExecuteAsync("""
                        DROP TRIGGER outbox_sequence ON waybill.outbox;
                        DROP TRIGGER outbox_terminal_guard ON waybill.outbox;
                        DROP INDEX waybill.ix_outbox_key_sequence;
                        DROP INDEX waybill.ix_outbox_blocked_keys;
                        """);
                }
                (baseline ? baseP99 : offP99).Add(await TransactionP99Async(database, baseline));
                (baseline ? baseRate : offRate).Add(await ClaimRateAsync(database, baseline ? V01ClaimSql : OutboxStore.ClaimSql));
                NpgsqlConnection.ClearAllPools(); // a database per variant: do not keep their connections
            }
        }

        var (bP99, oP99, bRate, oRate) = (Median(baseP99), Median(offP99), Median(baseRate), Median(offRate));
        var line = $"ordering off vs 0.1.0-alpha: transaction p99 {oP99:F1} vs {bP99:F1} ms ({(oP99 / bP99 - 1) * 100:+0.0;-0.0}%), claim {oRate:F0} vs {bRate:F0} rows/s ({(oRate / bRate - 1) * 100:+0.0;-0.0}%)";
        TestContext.Current.TestOutputHelper?.WriteLine(line);
        TestContext.Current.SendDiagnosticMessage(line);
        Assert.True(oP99 <= bP99 * 1.05, line);
        Assert.True(oRate >= bRate * 0.95, line);
    }

    // Producers committing one keyed message with an application row each; p99 of the SaveChanges.
    private static async Task<double> TransactionP99Async(TestDatabase database, bool baseline)
    {
        // Built the same way but for the key-list interceptor, which only AddDbContext lets AddWaybillOutbox add.
        var manual = new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(database.ConnectionString).Options;
        var collection = new ServiceCollection()
            .AddLogging()
            .AddWaybill(o =>
            {
                o.MaxPayloadBytes = 64 * 1024;
                o.AddMessage("billing.invoice-paid.v1", TestJson.Default.InvoicePaid);
            });
        if (baseline)
            collection.AddScoped(_ => new AppDbContext(manual)); // as in 0.1: no interceptor
        else
            collection.AddDbContext<AppDbContext>(o => o.UseNpgsql(database.ConnectionString));
        var services = collection.AddWaybillOutbox<AppDbContext>().BuildServiceProvider();
        await using var _ = services;
        var latencies = new System.Collections.Concurrent.ConcurrentBag<double>();
        var stop = Stopwatch.StartNew();
        await Task.WhenAll(Enumerable.Range(0, Producers).Select(p => Task.Run(async () =>
        {
            var random = new Random(p);
            while (stop.Elapsed < Window)
            {
                var watch = Stopwatch.StartNew();
                await using var scope = services.CreateAsyncScope();
                var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                context.Invoices.Add(new Invoice { Number = Guid.NewGuid().ToString("N"), Amount = 1m });
                scope.ServiceProvider.GetRequiredService<IOutbox<AppDbContext>>().Enqueue(new InvoicePaid(Guid.NewGuid(), 1m), $"invoice-{random.Next(100_000)}");
                await context.SaveChangesAsync();
                latencies.Add(watch.Elapsed.TotalMilliseconds);
            }
        })));
        var sorted = latencies.Order().ToArray();
        return sorted[(int)(sorted.Length * 0.99)];
    }

    // Claims of 100 handed back at once, over a backlog of 50 thousand rows; rows claimed per second.
    private static async Task<double> ClaimRateAsync(TestDatabase database, string claimSql)
    {
        await database.ExecuteAsync("""
            INSERT INTO waybill.outbox (id, type, key, key_hash, payload, content_type)
            SELECT ('00000000-0000-7000-8000-' || lpad(to_hex(g), 12, '0'))::uuid, 'billing.invoice-paid.v1', 'k-' || g, g, '\x7b7d', 'application/json'
            FROM generate_series(1, 50000) g
            """);
        await database.ExecuteAsync("VACUUM ANALYZE waybill.outbox");
        await using var dataSource = NpgsqlDataSource.Create(database.ConnectionString);
        var store = new OutboxStore(dataSource);
        var (rows, watch) = (0L, Stopwatch.StartNew());
        while (watch.Elapsed < Window / 2)
        {
            await using (var command = dataSource.CreateCommand(claimSql))
            {
                command.Parameters.Add(new NpgsqlParameter { Value = "measuring" });
                command.Parameters.Add(new NpgsqlParameter { Value = TimeSpan.FromSeconds(30), NpgsqlDbType = NpgsqlDbType.Interval });
                command.Parameters.Add(new NpgsqlParameter { Value = 100 });
                await using var reader = await command.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                    rows++;
            }
            await store.ReleaseOwnedAsync("measuring", CancellationToken.None);
        }
        return rows / watch.Elapsed.TotalSeconds;
    }

    private static double Median(List<double> values) => values.Order().ElementAt(values.Count / 2);
}
