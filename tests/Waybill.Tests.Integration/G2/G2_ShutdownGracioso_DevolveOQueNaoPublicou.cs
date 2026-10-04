using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using Waybill.EntityFrameworkCore.Dispatching;

namespace Waybill.Tests.Integration.G2;

// Graceful shutdown: stop claiming, let the batch in flight finish (bounded by PublishTimeout), and leave nothing
// claimed by this owner.
[Collection(PostgresCollection.Name)]
public sealed class G2_ShutdownGracioso_DevolveOQueNaoPublicou(PostgresFixture postgres)
{
    [Theory]
    [InlineData(true, 10)]   // the broker confirms during shutdown: the batch in flight is recorded as published
    [InlineData(false, 0)]   // it never confirms: the publish timeout hands the batch back
    public async Task G2_ShutdownGracioso_TerminaOLoteEmVooENadaFicaReivindicado(bool brokerConfirms, int expectedPublished)
    {
        var ct = TestContext.Current.CancellationToken;
        var database = await TestDatabase.CreateAsync(postgres);
        await DispatcherHarness.EnqueueAsync(database, 25);
        await using var dataSource = NpgsqlDataSource.Create(database.ConnectionString);
        var publishing = new TaskCompletionSource();
        var transport = new FakeTransport(async (_, batch, token) =>
        {
            publishing.TrySetResult();
            if (brokerConfirms)
                await Task.Delay(TimeSpan.FromMilliseconds(500), CancellationToken.None);
            else
                await Task.Delay(Timeout.Infinite, token);
            return batch.Select(_ => PublishResult.Confirmed).ToList();
        });
        var options = DispatcherHarness.Options(database, o =>
        {
            o.BatchSize = 10;
            o.PublishTimeout = TimeSpan.FromSeconds(1);
        });
        var dispatcher = DispatcherHarness.Create(dataSource, transport, options);
        var service = new WaybillDispatcherService(dispatcher, Options.Create(options), NullLogger<WaybillDispatcherService>.Instance);

        await service.StartAsync(ct);
        await publishing.Task.WaitAsync(TimeSpan.FromSeconds(30), ct);
        await service.StopAsync(ct);

        Assert.Equal(1, transport.Calls); // no new claim after shutdown began
        Assert.Equal(expectedPublished, await database.CountAsync("published"));
        Assert.Equal(0, await database.CountAsync("claimed"));
        Assert.Equal(25 - expectedPublished, await database.CountAsync("pending"));
    }
}
