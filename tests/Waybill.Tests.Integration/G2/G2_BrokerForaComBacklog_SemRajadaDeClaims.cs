using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using Waybill.EntityFrameworkCore.Dispatching;

namespace Waybill.Tests.Integration.G2;

// With the broker down and a backlog of full batches, the dispatcher must not claim, fail and claim again in a tight
// loop: consecutive transport failures back off (doubling, capped), and the first success resets to normal pace.
[Collection(PostgresCollection.Name)]
public sealed class G2_BrokerForaComBacklog_SemRajadaDeClaims(PostgresFixture postgres)
{
    [Fact]
    public async Task G2_BrokerForaComBacklog_SemRajadaDeClaims_RecuaEDepoisDrena()
    {
        var ct = TestContext.Current.CancellationToken;
        var database = await TestDatabase.CreateAsync(postgres);
        await DispatcherHarness.EnqueueAsync(database, 200);
        await using var dataSource = NpgsqlDataSource.Create(database.ConnectionString);
        var brokerUp = false;
        var transport = new FakeTransport((_, batch, _) => Volatile.Read(ref brokerUp)
            ? FakeTransport.ConfirmAll(batch)
            : throw new IOException("connection refused"));
        var options = DispatcherHarness.Options(database, o =>
        {
            o.BatchSize = 10;
            o.PollingInterval = TimeSpan.FromMilliseconds(50);
        });
        var service = new WaybillDispatcherService(
            DispatcherHarness.Create(dataSource, transport, options), Options.Create(options), NullLogger<WaybillDispatcherService>.Instance);

        await service.StartAsync(ct);
        await Task.Delay(TimeSpan.FromSeconds(2), ct);
        var callsWhileDown = transport.Calls;
        Volatile.Write(ref brokerUp, true);
        var deadline = DateTime.UtcNow.AddSeconds(60);
        while (DateTime.UtcNow < deadline && await database.CountAsync("published") < 200)
            await Task.Delay(100, ct);
        await service.StopAsync(ct);

        // Backoff 50, 100, 200, 400, 800 ms... fits about 6 attempts in 2 s; a tight loop would make hundreds.
        Assert.InRange(callsWhileDown, 2, 10);
        Assert.Equal(200, await database.CountAsync("published"));
        Assert.Equal(0, await database.ScalarAsync("SELECT coalesce(max(attempts), 0) FROM waybill.outbox"));
    }

    [Theory]
    [InlineData(0, 50)]
    [InlineData(1, 50)]
    [InlineData(2, 100)]
    [InlineData(5, 800)]
    [InlineData(30, 30_000)]
    public void Backoff_DobraAteOTeto(int consecutiveFailures, int expectedMilliseconds) =>
        Assert.Equal(TimeSpan.FromMilliseconds(expectedMilliseconds),
            WaybillDispatcherService.Wait(TimeSpan.FromMilliseconds(50), consecutiveFailures));
}
