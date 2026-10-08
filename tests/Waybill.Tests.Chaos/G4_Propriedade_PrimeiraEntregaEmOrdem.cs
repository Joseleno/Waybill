using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Waybill.EntityFrameworkCore;
using Waybill.Tests.Integration;
using Waybill.Tests.Integration.G2;
using Waybill.Tests.Integration.G4;

namespace Waybill.Tests.Chaos;

// G4 on publication (ADR 0008): per key, the first copy of each message to reach the broker arrives after the first copy
// of every earlier one, in commit order; later copies may come at any time. Producers commit concurrently, several keys
// per transaction; dispatchers come and go (graceful or dead); the broker stand-in sometimes stalls past the lease and
// delivers late (a copy from a publisher that gave up: what a zombie sends), sometimes stalls without delivering, and
// returns or refuses some messages; an operator releases the keys the DLQ stops. The stand-in numbers every delivery;
// the oracle reads only that log and the outbox. A released message is outside the stream: an earlier attempt left
// unconfirmed may still bring a copy of it at any time (here, a late copy of a message later refused and released),
// so released messages are counted apart, not checked for order.
[Collection(PostgresCollection.Name)]
public sealed class G4_Propriedade_PrimeiraEntregaEmOrdem(PostgresFixture postgres)
{
    private const int Keys = 20;

    [Fact]
    public Task G4_Propriedade_PrimeiraEntregaEmOrdem_Curto() => Run(TimeSpan.FromSeconds(20));

    [Fact]
    [Trait("Category", "Long")]
    public Task G4_Propriedade_PrimeiraEntregaEmOrdem_DezMinutos() => Run(TimeSpan.FromMinutes(10));

    private async Task Run(TimeSpan duration)
    {
        var ct = TestContext.Current.CancellationToken;
        var database = await TestDatabase.CreateAsync(postgres);
        var deliveries = new ConcurrentQueue<(long Order, OutgoingMessage Message)>();
        long deliveryCounter = 0;
        var (lateCopies, lostCalls, returns, defects) = (0, 0, 0, 0);
        var calm = 0; // the drain at the end: no more stalls nor returns

        void Deliver(OutgoingMessage message) => deliveries.Enqueue((Interlocked.Increment(ref deliveryCounter), message));

        var transport = new FakeTransport(async (_, batch, _) =>
        {
            var roll = Random.Shared.NextDouble();
            if (Volatile.Read(ref calm) == 0 && roll < 0.04)
            {
                Interlocked.Increment(ref lateCopies);
                await Task.Delay(TimeSpan.FromMilliseconds(900), CancellationToken.None); // the dispatcher gave up long ago
                foreach (var message in batch)
                    Deliver(message);
                return batch.Select(_ => PublishResult.Confirmed).ToList();
            }
            if (Volatile.Read(ref calm) == 0 && roll < 0.08)
            {
                Interlocked.Increment(ref lostCalls);
                await Task.Delay(TimeSpan.FromMilliseconds(900), CancellationToken.None); // never reached the broker
                return batch.Select(_ => PublishResult.Retry).ToList();
            }
            var results = new List<PublishResult>(batch.Count);
            foreach (var message in batch)
            {
                if (Amount(message) % 100 == 0)
                {
                    Interlocked.Increment(ref defects);
                    results.Add(PublishResult.Defect("refused by the broker stand-in"));
                }
                else if (Volatile.Read(ref calm) == 0 && Random.Shared.NextDouble() < 0.02)
                {
                    Interlocked.Increment(ref returns);
                    results.Add(new PublishResult(PublishStatus.Returned, "312 NO_ROUTE"));
                }
                else
                {
                    Deliver(message); // before the confirmation: the dispatcher marks only what the broker has
                    results.Add(PublishResult.Confirmed);
                }
            }
            return results;
        });
        var options = OrderingHarness.Options(database, partitions: 8, partitionLease: TimeSpan.FromMilliseconds(700));
        options.BatchSize = 50;
        options.PublishTimeout = TimeSpan.FromMilliseconds(200);
        options.LeaseMargin = TimeSpan.FromMilliseconds(150); // row lease 350 ms, partition lease twice that
        options.ReturnBackoff = TimeSpan.FromMilliseconds(100);
        options.MaxReturnBackoff = TimeSpan.FromMilliseconds(200);
        options.MaxReturns = 4;

        await using var dataSource = NpgsqlDataSource.Create(database.ConnectionString);
        Assert.Null(await OrderingHarness.Create(dataSource, transport, options).CheckOrderingAsync(atStartup: true, ct));

        var (graceful, deaths) = (0, 0);
        var clock = Stopwatch.StartNew();
        var slots = Enumerable.Range(0, 6).Select(_ => Task.Run(async () =>
        {
            while (clock.Elapsed < duration)
            {
                var dispatcher = OrderingHarness.Create(dataSource, transport, options);
                var lifetime = Stopwatch.StartNew();
                var life = TimeSpan.FromMilliseconds(Random.Shared.Next(500, 3_000));
                while (lifetime.Elapsed < life && clock.Elapsed < duration)
                {
                    if ((await dispatcher.RunOnceAsync(CancellationToken.None)).Claimed == 0)
                        await Task.Delay(20, CancellationToken.None);
                }
                if (Random.Shared.NextDouble() < 0.5)
                {
                    await dispatcher.ReleaseOwnedAsync(CancellationToken.None);
                    Interlocked.Increment(ref graceful);
                }
                else
                {
                    Interlocked.Increment(ref deaths);
                }
                await Task.Delay(Random.Shared.Next(0, 300), CancellationToken.None);
            }
        }, CancellationToken.None)).ToList();

        var amount = 0;
        await using var services = database.Services();
        var producers = Enumerable.Range(0, 4).Select(p => Task.Run(async () =>
        {
            var random = new Random(p);
            while (clock.Elapsed < duration)
            {
                await using var scope = services.CreateAsyncScope();
                var outbox = scope.ServiceProvider.GetRequiredService<IOutbox<AppDbContext>>();
                for (var i = random.Next(1, 4); i > 0; i--)
                {
                    var key = random.Next(10) == 0 ? null : $"key-{random.Next(Keys)}";
                    outbox.Enqueue(new InvoicePaid(Guid.NewGuid(), Interlocked.Increment(ref amount)), key);
                }
                await scope.ServiceProvider.GetRequiredService<AppDbContext>().SaveChangesAsync(CancellationToken.None);
                await Task.Delay(random.Next(0, 15), CancellationToken.None);
            }
        }, CancellationToken.None)).ToList();
        // An operator releasing the keys the DLQ stops, now and then: the key moves on, leaving a gap.
        var released = 0;
        var operatorTask = Task.Run(async () =>
        {
            while (clock.Elapsed < duration)
            {
                await Task.Delay(1_000, CancellationToken.None);
                released += (int)await database.ScalarAsync("""
                    WITH r AS (UPDATE waybill.outbox SET status = 'released', released_at = clock_timestamp(), released_by = 'chaos'
                               WHERE status = 'dlq' AND sequence IS NOT NULL RETURNING 1)
                    SELECT count(*) FROM r
                    """);
            }
        }, CancellationToken.None);
        await Task.WhenAll(producers.Concat(slots).Append(operatorTask));

        // Drain with one survivor, the broker stand-in calm, once every dead instance's leases ran out.
        Volatile.Write(ref calm, 1);
        var survivor = OrderingHarness.Create(dataSource, transport, options);
        var drain = Stopwatch.StartNew();
        while (await ClaimableAsync(database) > 0 && drain.Elapsed < TimeSpan.FromMinutes(2))
        {
            if ((await survivor.RunOnceAsync(ct)).Claimed == 0)
                await Task.Delay(100, ct);
        }
        await Task.Delay(TimeSpan.FromSeconds(1), ct); // let the last abandoned calls deliver their late copies

        var (sequenceOf, releasedIds) = await SequencesAsync(dataSource);
        var firstDeliveries = deliveries.OrderBy(d => d.Order).DistinctBy(d => d.Message.MessageId).ToList();
        var inversions = 0;
        var releasedDelivered = firstDeliveries.Count(d => releasedIds.Contains(d.Message.MessageId));
        foreach (var key in firstDeliveries.Where(d => d.Message.Key is not null && !releasedIds.Contains(d.Message.MessageId)).GroupBy(d => d.Message.Key))
        {
            var sequences = key.Select(d => sequenceOf[d.Message.MessageId]).ToList();
            inversions += sequences.Zip(sequences.Skip(1)).Count(pair => pair.Second <= pair.First);
        }
        var envelopeMismatches = deliveries.Count(d => d.Message.Sequence != sequenceOf[d.Message.MessageId]);
        var stranded = await ClaimableAsync(database);
        var published = await database.CountAsync("published");
        var undelivered = await database.ScalarAsync(
            $"SELECT count(*) FROM waybill.outbox WHERE status = 'published' AND NOT (id = ANY('{{{string.Join(',', firstDeliveries.Select(d => d.Message.MessageId))}}}'::uuid[]))");

        TestContext.Current.SendDiagnosticMessage(
            $"produced {amount}, published {published}, released {released} (delivered anyway {releasedDelivered}), deliveries {deliveries.Count} ({firstDeliveries.Count} first), late copies {lateCopies}, lost calls {lostCalls}, returns {returns}, defects {defects}, graceful {graceful}, deaths {deaths}, inversions {inversions}");

        Assert.Equal(0, inversions);         // first copies per key in commit order, released messages aside
        Assert.Equal(0, envelopeMismatches); // the transport saw the stored sequence (null when not ordered)
        Assert.Equal(0, stranded);           // nothing left that could still go: only what a DLQ head stops
        Assert.Equal(0, undelivered);        // nothing marked published that the broker never had
        Assert.Equal(0, await database.ScalarAsync("SELECT count(*) FROM waybill.outbox WHERE status = 'claimed'"));
        Assert.True(lateCopies > 0 && lostCalls > 0 && defects > 0 && returns > 0 && graceful > 0 && deaths > 0 && released > 0,
            $"late copies {lateCopies}, lost calls {lostCalls}, defects {defects}, returns {returns}, graceful {graceful}, deaths {deaths}, released {released}");
    }

    private static int Amount(OutgoingMessage message) =>
        JsonDocument.Parse(message.Payload).RootElement.GetProperty("Amount").GetInt32();

    // Rows that could still be published: pending or claimed, and not behind a DLQ head of their key.
    private static Task<long> ClaimableAsync(TestDatabase database) => database.ScalarAsync("""
        SELECT count(*) FROM waybill.outbox o
        WHERE o.status IN ('pending', 'claimed')
          AND NOT (o.sequence IS NOT NULL AND EXISTS (
                SELECT 1 FROM waybill.outbox d WHERE d.key = o.key AND d.sequence < o.sequence AND d.status = 'dlq'))
        """);

    private static async Task<(Dictionary<Guid, long?> Sequences, HashSet<Guid> Released)> SequencesAsync(NpgsqlDataSource dataSource)
    {
        await using var command = dataSource.CreateCommand("SELECT id, sequence, status = 'released' FROM waybill.outbox");
        await using var reader = await command.ExecuteReaderAsync();
        var sequences = new Dictionary<Guid, long?>();
        var released = new HashSet<Guid>();
        while (await reader.ReadAsync())
        {
            sequences[reader.GetGuid(0)] = reader.IsDBNull(1) ? null : reader.GetInt64(1);
            if (reader.GetBoolean(2))
                released.Add(reader.GetGuid(0));
        }
        return (sequences, released);
    }
}
