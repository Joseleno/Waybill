using Microsoft.Extensions.Time.Testing;
using Npgsql;
using Waybill.Tests.Integration.G2;

namespace Waybill.Tests.Integration.G4;

// With ordering on, a keyed message is published only by the dispatcher that holds its partition key_hash % P;
// a message without a key is not ordered and goes out through whichever dispatcher claims it first (ADR 0007).
[Collection(PostgresCollection.Name)]
public sealed class G4_ClaimSoDasParticoesDaInstancia(PostgresFixture postgres)
{
    private const int P = 4;

    [Fact]
    public async Task G4_ClaimSoDasParticoesDaInstancia_SemChaveSaiPorQualquerUma()
    {
        var ct = TestContext.Current.CancellationToken;
        var database = await TestDatabase.CreateAsync(postgres);
        await using var dataSource = NpgsqlDataSource.Create(database.ConnectionString);
        var options = OrderingHarness.Options(database, P);
        var (transportA, transportB) = (new FakeTransport(), new FakeTransport());
        var time = new FakeTimeProvider(); // each round below is due for partition upkeep
        var a = OrderingHarness.Create(dataSource, transportA, options, time);
        var b = OrderingHarness.Create(dataSource, transportB, options, time);
        Assert.Null(await a.CheckOrderingAsync(atStartup: true, ct));

        // A takes all four alone; with B alive the share drops to two: A hands two back, B takes them.
        foreach (var dispatcher in new[] { a, b, a, b })
        {
            await dispatcher.RunOnceAsync(ct);
            time.Advance(TimeSpan.FromMinutes(1));
        }
        Assert.Equal(2, a.HeldPartitions.Count);
        Assert.Equal(2, b.HeldPartitions.Count);
        Assert.Empty(a.HeldPartitions.Keys.Intersect(b.HeldPartitions.Keys));

        await DispatcherHarness.EnqueueAsync(database, 40);
        await DispatcherHarness.EnqueueAsync(database, 10, key: _ => null);
        var partitionOf = await OrderingHarness.PartitionOfAsync(database, P);
        await a.RunOnceAsync(ct);
        await b.RunOnceAsync(ct);

        Assert.Equal(50, await database.CountAsync("published"));
        Assert.All(transportA.Received.Where(m => m.Key is not null), m => Assert.Contains(partitionOf[m.MessageId], a.HeldPartitions.Keys));
        Assert.All(transportB.Received.Where(m => m.Key is not null), m => Assert.Contains(partitionOf[m.MessageId], b.HeldPartitions.Keys));
        Assert.NotEmpty(transportB.Received);
        Assert.Equal(10, transportA.Received.Count(m => m.Key is null)); // A ran first and took every keyless message
    }
}
