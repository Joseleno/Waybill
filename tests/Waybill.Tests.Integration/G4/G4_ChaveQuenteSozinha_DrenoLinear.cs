using Npgsql;
using Waybill.Tests.Integration.G2;

namespace Waybill.Tests.Integration.G4;

// One hot key and nothing else (PLAN.md, "Ponteiro de cabeça"). Without the head pointer every claim read the key's whole
// queue trying to fill the batch, since only the head can go: O(n) per claim, O(n²) to drain the key (measured: 100
// thousand pending, 109 ms per claim). With it, only the head is a candidate, and each publication marks the next one:
// the claim reads the same few buffers whatever the queue behind.
[Collection(PostgresCollection.Name)]
public sealed class G4_ChaveQuenteSozinha_DrenoLinear(PostgresFixture postgres)
{
    private const int Drained = 50;

    [Theory]
    [InlineData(1_000)]
    [InlineData(4_000)]
    public async Task G4_ChaveQuenteSozinha_DrenoLinear_ClaimNaoLeAFila(int pending)
    {
        var ct = TestContext.Current.CancellationToken;
        var database = await TestDatabase.CreateAsync(postgres);
        await using var dataSource = NpgsqlDataSource.Create(database.ConnectionString);
        var transport = new FakeTransport();
        var dispatcher = OrderingHarness.Create(dataSource, transport, OrderingHarness.Options(database, partitions: 1));
        Assert.Null(await dispatcher.CheckOrderingAsync(atStartup: true, ct));
        Assert.Equal(0, (await dispatcher.RunOnceAsync(ct)).Claimed); // takes the partition
        await DispatcherHarness.EnqueueAsync(database, pending, key: _ => "hot");
        await database.ExecuteAsync("VACUUM ANALYZE waybill.outbox");

        var before = await OrderingHarness.ClaimBuffersAsync(dataSource, dispatcher.Owner, partitions: 1);
        for (var i = 0; i < Drained; i++)
            Assert.Equal(1, (await dispatcher.RunOnceAsync(ct)).Claimed);
        var after = await OrderingHarness.ClaimBuffersAsync(dataSource, dispatcher.Owner, partitions: 1);

        TestContext.Current.TestOutputHelper?.WriteLine($"{pending} pending of one key: {before} buffers per claim, {after} after {Drained} published");
        Assert.Equal(Enumerable.Range(1, Drained).Select(s => (long?)s), transport.Received.Select(m => m.Sequence));
        Assert.True(before < 100, $"{before} buffers for one key with {pending} pending");
        Assert.True(after < 100, $"{after} buffers for one key with {pending - Drained} pending");
    }
}
