using Microsoft.Extensions.Time.Testing;
using Npgsql;
using Waybill.EntityFrameworkCore.Dispatching;

namespace Waybill.Tests.Integration.G2;

// ADR 0003, edges of the breaker: it reacts only to what the broker did, and a silent outage (every publish times
// out, no connection error ever) still opens it.
[Collection(PostgresCollection.Name)]
public sealed class G2_BreakerReageSoAoBroker(PostgresFixture postgres)
{
    // Half-open, the probe is the oldest row. If that row turns out to be a local defect (written under a larger
    // payload limit), the cycle never reached the broker: the breaker must stay half-open and probe again, not close.
    [Fact]
    public async Task G2_SondaSoComDefeitoLocal_NaoFechaOBreaker()
    {
        var ct = TestContext.Current.CancellationToken;
        var database = await TestDatabase.CreateAsync(postgres);
        var ids = await DispatcherHarness.EnqueueAsync(database, 4, i => new InvoicePaid(Guid.NewGuid(), i == 0 ? 1e20m : i));
        var oversize = ids[0]; // the only payload above the limit below
        var limit = (int)await database.ScalarAsync($"SELECT length(payload) - 1 FROM waybill.outbox WHERE id = '{oversize}'");
        await using var dataSource = NpgsqlDataSource.Create(database.ConnectionString);
        var brokerUp = false;
        var transport = new FakeTransport((_, batch, _) => Volatile.Read(ref brokerUp)
            ? FakeTransport.ConfirmAll(batch)
            : Task.FromResult<IReadOnlyList<PublishResult>>(batch.Select(_ => PublishResult.RetryAfter(TransportFailure.Connection)).ToList()));
        var time = new FakeTimeProvider();
        var dispatcher = DispatcherHarness.Create(dataSource, transport,
            DispatcherHarness.Options(database, o => o.PollingInterval = TimeSpan.FromSeconds(1)), maxPayloadBytes: limit, time: time);

        // The first batch takes all four: the oversize one goes to the DLQ locally, the other three fail on the
        // connection and open the breaker.
        Assert.Equal(DispatchOutcome.ConnectionFailure, (await dispatcher.RunOnceAsync(ct)).Outcome);
        Assert.Equal(1, await database.CountAsync("dlq"));

        // Make the half-open probe (the oldest pending row) a local defect too.
        var pending = ids.Where(id => id != oversize).Order().ToList();
        await database.ScalarAsync($"WITH u AS (UPDATE waybill.outbox SET payload = (SELECT payload FROM waybill.outbox WHERE id = '{oversize}') WHERE id = '{pending[0]}' RETURNING 1) SELECT count(*) FROM u");
        time.Advance(TimeSpan.FromSeconds(1));
        var probe = await dispatcher.RunOnceAsync(ct);
        Assert.Equal((1, DispatchOutcome.Progress), Pair(probe)); // it reached no broker...

        var nextProbe = await dispatcher.RunOnceAsync(ct); // ...so the breaker is still half-open: one message again,
        Assert.Equal((1, 1, DispatchOutcome.ConnectionFailure), (nextProbe.BatchSize, nextProbe.Claimed, nextProbe.Outcome)); // and the broker is still down

        Volatile.Write(ref brokerUp, true);
        time.Advance(TimeSpan.FromSeconds(2));
        Assert.Equal((1, DispatchOutcome.Progress), Pair(await dispatcher.RunOnceAsync(ct))); // probe goes through: closed
        Assert.Equal((1, DispatchOutcome.Progress), Pair(await dispatcher.RunOnceAsync(ct)));
        Assert.Equal(2, await database.CountAsync("published"));
        Assert.Equal(2, await database.CountAsync("dlq"));
    }

    // A network that drops packets silently never raises a connection error: publishes only time out. Back-pressure
    // first (the batch shrinks to one), then, after SilentOutageThreshold timeouts in a row at one, the breaker opens.
    [Fact]
    public async Task G2_QuedaSilenciosa_TimeoutsComLoteDe1AbremOBreaker()
    {
        var ct = TestContext.Current.CancellationToken;
        var database = await TestDatabase.CreateAsync(postgres);
        await DispatcherHarness.EnqueueAsync(database, 10);
        await using var dataSource = NpgsqlDataSource.Create(database.ConnectionString);
        var transport = new FakeTransport((_, batch, _) => Task.FromResult<IReadOnlyList<PublishResult>>(
            batch.Select(_ => PublishResult.RetryAfter(TransportFailure.ConfirmTimeout)).ToList()));
        var dispatcher = DispatcherHarness.Create(dataSource, transport, DispatcherHarness.Options(database, o => o.BatchSize = 2), time: new FakeTimeProvider());

        var outcomes = new List<DispatchOutcome>();
        for (var cycle = 0; cycle < 6; cycle++)
            outcomes.Add((await dispatcher.RunOnceAsync(ct)).Outcome);

        // 2 -> 1 (pressure), then three timeouts at one: the third opens the breaker; then it stays open.
        Assert.Equal(
            [DispatchOutcome.Pressure, DispatchOutcome.Pressure, DispatchOutcome.Pressure, DispatchOutcome.ConnectionFailure,
             DispatchOutcome.BreakerOpen, DispatchOutcome.BreakerOpen],
            outcomes);
        Assert.Equal(0, await database.CountAsync("dlq"));
        Assert.Equal(0, await database.ScalarAsync("SELECT coalesce(max(attempts), 0) FROM waybill.outbox"));
    }

    // ADR 0003: a probe that fails reopens the breaker for twice as long. During a silent outage the probe fails by
    // timing out, not by a connection error; closing the breaker on it would cycle the backlog through claims and
    // hand-backs at the base period, which is exactly what the breaker is there to stop.
    [Fact]
    public async Task G2_QuedaSilenciosa_SondaQueEstouraOTimeout_ReabrePeloDobro()
    {
        var ct = TestContext.Current.CancellationToken;
        var database = await TestDatabase.CreateAsync(postgres);
        await DispatcherHarness.EnqueueAsync(database, 10);
        await using var dataSource = NpgsqlDataSource.Create(database.ConnectionString);
        var transport = new FakeTransport((_, batch, _) => Task.FromResult<IReadOnlyList<PublishResult>>(
            batch.Select(_ => PublishResult.RetryAfter(TransportFailure.ConfirmTimeout)).ToList()));
        var time = new FakeTimeProvider();
        var dispatcher = DispatcherHarness.Create(dataSource, transport,
            DispatcherHarness.Options(database, o => { o.BatchSize = 1; o.PollingInterval = TimeSpan.FromSeconds(1); }), time: time);
        for (var cycle = 0; cycle < 3; cycle++)
            await dispatcher.RunOnceAsync(ct);
        Assert.Equal(DispatchOutcome.BreakerOpen, (await dispatcher.RunOnceAsync(ct)).Outcome); // opened for 1 s

        time.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal((1, DispatchOutcome.ConnectionFailure), Pair(await dispatcher.RunOnceAsync(ct))); // the probe timed out: reopened...
        time.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal((0, DispatchOutcome.BreakerOpen), Pair(await dispatcher.RunOnceAsync(ct))); // ...for 2 s, not 1
        time.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal((1, DispatchOutcome.ConnectionFailure), Pair(await dispatcher.RunOnceAsync(ct))); // the next probe
        Assert.Equal(0, await database.ScalarAsync("SELECT coalesce(max(attempts), 0) FROM waybill.outbox"));
    }

    private static (int, DispatchOutcome) Pair(DispatchCycle cycle) => (cycle.Claimed, cycle.Outcome);
}
