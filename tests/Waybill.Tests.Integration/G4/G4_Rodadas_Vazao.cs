using System.Diagnostics;
using Npgsql;
using Waybill.EntityFrameworkCore.Dispatching;
using Waybill.Tests.Integration.G2;

namespace Waybill.Tests.Integration.G4;

// What M buys one busy key, and what false positives of the head pointer cost (PLAN.md 8c-2), for OPERATIONS.md.
[Collection(PostgresCollection.Name)]
public sealed class G4_Rodadas_Vazao(PostgresFixture postgres)
{
    private const int Queue = 600;

    // One key alone, a broker that takes 2 ms to confirm each call. With M = 1 each message costs a cycle: claim,
    // publication, marking. With M = 20 a cycle carries 20 of them in 20 rounds: one claim and one marking for the lot.
    // Asserted in cycles; the rate is recorded.
    [Theory]
    [InlineData(1)]
    [InlineData(20)]
    public async Task G4_Rodadas_VazaoPorChave_Medida(int maxPerKey)
    {
        var ct = TestContext.Current.CancellationToken;
        var database = await TestDatabase.CreateAsync(postgres);
        await using var dataSource = NpgsqlDataSource.Create(database.ConnectionString);
        var transport = new FakeTransport((_, batch, _) =>
        {
            var confirming = Stopwatch.StartNew(); // a timer would round 2 ms up to the clock tick (about 15 ms on Windows)
            while (confirming.Elapsed < TimeSpan.FromMilliseconds(2))
                Thread.SpinWait(100);
            return FakeTransport.ConfirmAll(batch);
        });
        var dispatcher = OrderingHarness.Create(dataSource, transport, OrderingHarness.Options(database, partitions: 1, maxPerKey: maxPerKey));
        Assert.Null(await dispatcher.CheckOrderingAsync(atStartup: true, ct));
        Assert.Equal(0, (await dispatcher.RunOnceAsync(ct)).Claimed);
        await DispatcherHarness.EnqueueAsync(database, Queue, key: _ => "hot");

        var cycles = 0;
        var watch = Stopwatch.StartNew();
        while (await database.CountAsync("published") < Queue)
        {
            await dispatcher.RunOnceAsync(ct);
            cycles++;
        }
        watch.Stop();

        var line = $"one key, {Queue} messages, confirmation 2 ms per call, M = {maxPerKey}: {cycles} cycles, {Queue / watch.Elapsed.TotalSeconds:F0} messages/s";
        TestContext.Current.TestOutputHelper?.WriteLine(line);
        TestContext.Current.SendDiagnosticMessage(line);
        Assert.Equal((Queue + maxPerKey - 1) / maxPerKey, cycles);
        Assert.Equal(Enumerable.Range(1, Queue).Select(s => (long?)s), transport.Received.Select(m => m.Sequence));
    }

    // A limit, measured: every row of a key marked (a producer writing a busy key under REPEATABLE READ, or ordering turned
    // off and on again while keys were stopped in the DLQ) puts the claim back where it was before the head pointer: it
    // reads the stopped keys' queues on every cycle, until they are released. Ordering stays correct (OPERATIONS.md;
    // issue #21 for clearing such marks).
    [Fact]
    public async Task Limite_FalsosPositivosPersistentes_ClaimLeAFilaDasChavesParadas()
    {
        var database = await TestDatabase.CreateAsync(postgres);
        // 2000 keys stopped by the DLQ with 50 behind each, every row marked; 100 live keys after them in id order.
        await database.ExecuteAsync("""
            INSERT INTO waybill.outbox (id, type, key, key_hash, sequence, payload, content_type, status, attempts, dlq_reason, is_head)
            SELECT ('00000000-0000-7000-8000-' || lpad(to_hex(s * 2000 + k), 12, '0'))::uuid, 'billing.invoice-paid.v1', 'stopped-' || k, 0, s + 1,
                   '\x7b7d', 'application/json', CASE WHEN s = 0 THEN 'dlq' ELSE 'pending' END, CASE WHEN s = 0 THEN 1 ELSE 0 END,
                   CASE WHEN s = 0 THEN 'defect' END, true
            FROM generate_series(0, 1999) k, generate_series(0, 50) s;
            INSERT INTO waybill.outbox_keys SELECT 'stopped-' || k, 51 FROM generate_series(0, 1999) k;
            INSERT INTO waybill.outbox (id, type, key, key_hash, sequence, payload, content_type)
            SELECT ('00000000-0000-7000-9000-' || lpad(to_hex(k), 12, '0'))::uuid, 'billing.invoice-paid.v1', 'live-' || k, 0, 1, '\x7b7d', 'application/json'
            FROM generate_series(0, 99) k;
            INSERT INTO waybill.outbox_keys SELECT 'live-' || k, 1 FROM generate_series(0, 99) k;
            """);
        await database.ExecuteAsync("VACUUM ANALYZE waybill.outbox");
        await using var dataSource = NpgsqlDataSource.Create(database.ConnectionString);
        await G4_Rodadas_Claim.OwnAllPartitionsAsync(database);
        var withFalsePositives = await OrderingHarness.ClaimBuffersAsync(dataSource, "instance", partitions: 4);

        await OrderingHarness.MarkHeadsAsync(database); // what the triggers would have left
        await database.ExecuteAsync("VACUUM ANALYZE waybill.outbox");
        var marked = await OrderingHarness.ClaimBuffersAsync(dataSource, "instance", partitions: 4);

        var line = $"2000 keys stopped with 50 behind, 100 live: claim {withFalsePositives} buffers with every row marked, {marked} with only the heads";
        TestContext.Current.TestOutputHelper?.WriteLine(line);
        TestContext.Current.SendDiagnosticMessage(line);
        Assert.True(withFalsePositives > 20 * marked, line);
    }
}
