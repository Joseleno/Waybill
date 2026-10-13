using Npgsql;
using Waybill.EntityFrameworkCore.Dispatching;
using Waybill.Tests.Integration.G2;
using Waybill.Tests.Integration.G4;

namespace Waybill.Tests.Integration;

// OPERATIONS.md changes P without deleting waybill.settings: the database keeps numbering keyed messages throughout.
// Deleting the settings, as the first procedure did, left messages written meanwhile unnumbered, outside the key's
// order. This test runs the documented SQL while the applications keep enqueuing.
[Collection(PostgresCollection.Name)]
public sealed class Operacao_MudarP(PostgresFixture postgres)
{
    [Fact]
    public async Task Operacao_SqlDoOperationsMudaP_ComEscritaConcorrente_NenhumaLinhaSemSequence()
    {
        var ct = TestContext.Current.CancellationToken;
        var database = await TestDatabase.CreateAsync(postgres);
        await using var dataSource = NpgsqlDataSource.Create(database.ConnectionString);
        var before = OrderingHarness.Create(dataSource, new FakeTransport(), OrderingHarness.Options(database, partitions: 4));
        Assert.Null(await before.CheckOrderingAsync(atStartup: true, ct));
        await before.RunOnceAsync(ct);

        using var stop = new CancellationTokenSource();
        var writer = Task.Run(async () =>
        {
            var written = 0;
            while (!stop.IsCancellationRequested)
            {
                await DispatcherHarness.EnqueueAsync(database, 3, key: i => $"order-{i}");
                written += 3;
            }
            return written;
        });
        await Task.Delay(200, ct);
        await using (var command = dataSource.CreateCommand(OperationsDoc.Sql("ordering-change")))
            await command.ExecuteNonQueryAsync(ct);
        await Task.Delay(200, ct);
        await stop.CancelAsync();
        var written = await writer;

        Assert.True(written > 0);
        Assert.Equal(0, await database.ScalarAsync("SELECT count(*) FROM waybill.outbox WHERE key IS NOT NULL AND sequence IS NULL"));
        Assert.Equal(new OrderingSettings(32, TimeSpan.FromSeconds(60)), await new PartitionStore(dataSource).ReadSettingsAsync(ct));
        var after = OrderingHarness.Create(dataSource, new FakeTransport(), OrderingHarness.Options(database, partitions: 32));
        Assert.Null(await after.CheckOrderingAsync(atStartup: true, ct));
        await after.RunOnceAsync(ct);
        Assert.Equal(32, after.HeldPartitions.Count);
        Assert.Contains("Partitions = 32", await before.CheckOrderingAsync(atStartup: false, ct)); // the old one would stop
    }
}
