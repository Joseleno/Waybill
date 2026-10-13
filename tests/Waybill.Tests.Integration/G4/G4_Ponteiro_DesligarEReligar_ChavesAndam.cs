using Npgsql;
using Waybill.Tests.Integration.G2;

namespace Waybill.Tests.Integration.G4;

// Turning ordering off (OPERATIONS.md, <!-- ordering-reset -->) leaves numbered rows behind, some unmarked behind their
// key's head. The claim without ordering walks the same index, so the reset marks them: with ordering off a DLQ row
// stops nothing, and the rows behind one would otherwise never be claimed (the sondagem saw 5 thousand of 130 thousand
// visible). Turned on again, keys move in order from their next row.
[Collection(PostgresCollection.Name)]
public sealed class G4_Ponteiro_DesligarEReligar_ChavesAndam(PostgresFixture postgres)
{
    [Fact]
    public async Task G4_Ponteiro_DesligarComCabecaNaDlq_SucessorasSaem_ReligarSegueEmOrdem()
    {
        var ct = TestContext.Current.CancellationToken;
        var database = await TestDatabase.CreateAsync(postgres);
        await using var dataSource = NpgsqlDataSource.Create(database.ConnectionString);
        var ordered = OrderingHarness.Create(dataSource, new FakeTransport(), OrderingHarness.Options(database, partitions: 1));
        Assert.Null(await ordered.CheckOrderingAsync(atStartup: true, ct));
        await ordered.RunOnceAsync(ct);
        await DispatcherHarness.EnqueueAsync(database, 3, key: _ => "K");
        await DispatcherHarness.EnqueueAsync(database, 3, key: _ => "J");
        await database.ExecuteAsync("UPDATE waybill.outbox SET status = 'dlq', dlq_reason = 'defect' WHERE key = 'K' AND sequence = 1");

        await database.ExecuteAsync(OperationsDoc.Sql("ordering-reset"));
        var unordered = DispatcherHarness.Create(dataSource, new FakeTransport(), DispatcherHarness.Options(database));
        Assert.Null(await unordered.CheckOrderingAsync(atStartup: true, ct));
        await unordered.RunOnceAsync(ct);

        Assert.Equal(5, await database.CountAsync("published")); // all but K's DLQ row, which stops nothing now

        var transport = new FakeTransport();
        var again = OrderingHarness.Create(dataSource, transport, OrderingHarness.Options(database, partitions: 1));
        Assert.Null(await again.CheckOrderingAsync(atStartup: true, ct));
        await again.RunOnceAsync(ct);
        await DispatcherHarness.EnqueueAsync(database, 2, key: _ => "J");
        for (var i = 0; i < 3; i++)
            await again.RunOnceAsync(ct);

        Assert.Equal([4L, 5L], transport.Received.Where(m => m.Key == "J").Select(m => m.Sequence ?? -1));
    }
}
