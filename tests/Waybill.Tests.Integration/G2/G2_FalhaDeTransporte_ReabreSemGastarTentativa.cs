using Microsoft.Extensions.Time.Testing;
using Npgsql;
using Waybill.EntityFrameworkCore.Dispatching;

namespace Waybill.Tests.Integration.G2;

// G2: a transport failure hands the batch back at once and spends no attempt; the broker being down never moves a
// message toward the DLQ. A connection failure (or one of unstated cause) opens the breaker: no claims while open,
// then a probe with one message, and the backlog flows again once the probe goes through.
[Collection(PostgresCollection.Name)]
public sealed class G2_FalhaDeTransporte_ReabreSemGastarTentativa(PostgresFixture postgres)
{
    [Theory]
    [InlineData("exception")]
    [InlineData("retry")]
    public async Task G2_FalhaDeTransporte_ReabreSemGastarTentativa_BreakerSondaEDepoisPublica(string failure)
    {
        var ct = TestContext.Current.CancellationToken;
        var database = await TestDatabase.CreateAsync(postgres);
        await DispatcherHarness.EnqueueAsync(database, 3);
        await using var dataSource = NpgsqlDataSource.Create(database.ConnectionString);
        var transport = new FakeTransport((call, batch, _) => call switch
        {
            <= 2 when failure == "exception" => throw new IOException("connection reset by broker"),
            <= 2 => Task.FromResult<IReadOnlyList<PublishResult>>(batch.Select(_ => PublishResult.Retry).ToList()),
            _ => FakeTransport.ConfirmAll(batch),
        });
        var time = new FakeTimeProvider();
        var dispatcher = DispatcherHarness.Create(dataSource, transport,
            DispatcherHarness.Options(database, o => o.PollingInterval = TimeSpan.FromSeconds(1)), time: time);

        var first = await dispatcher.RunOnceAsync(ct);
        Assert.Equal((3, DispatchOutcome.ConnectionFailure), (first.Claimed, first.Outcome));
        Assert.Equal(3, await database.ScalarAsync("SELECT count(*) FROM waybill.outbox WHERE status = 'pending' AND attempts = 0 AND lease_until IS NULL"));

        Assert.Equal(DispatchOutcome.BreakerOpen, (await dispatcher.RunOnceAsync(ct)).Outcome); // nothing claimed while open
        Assert.Equal(1, transport.Calls);

        time.Advance(TimeSpan.FromSeconds(1));
        var probe = await dispatcher.RunOnceAsync(ct); // half-open: one message, and it fails again
        Assert.Equal((1, DispatchOutcome.ConnectionFailure), (probe.Claimed, probe.Outcome));

        time.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(DispatchOutcome.BreakerOpen, (await dispatcher.RunOnceAsync(ct)).Outcome); // reopened for 2 s
        time.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal((1, DispatchOutcome.Progress), Pair(await dispatcher.RunOnceAsync(ct))); // probe goes through: closed
        Assert.Equal((2, DispatchOutcome.Progress), Pair(await dispatcher.RunOnceAsync(ct))); // the rest of the backlog

        Assert.Equal(3, await database.CountAsync("published"));
        Assert.Equal(0, await database.ScalarAsync("SELECT coalesce(max(attempts), 0) FROM waybill.outbox"));
    }

    private static (int, DispatchOutcome) Pair(DispatchCycle cycle) => (cycle.Claimed, cycle.Outcome);

    // A buggy transport returning default(PublishResult) or an undefined status must never mark a message as
    // published without a broker confirmation: it is a transport failure.
    [Theory]
    [InlineData(0)]
    [InlineData(99)]
    public async Task G2_ResultadoInvalidoDoTransporte_NuncaContaComoConfirmado(int status)
    {
        var ct = TestContext.Current.CancellationToken;
        var database = await TestDatabase.CreateAsync(postgres);
        await DispatcherHarness.EnqueueAsync(database, 3);
        await using var dataSource = NpgsqlDataSource.Create(database.ConnectionString);
        var transport = new FakeTransport((_, batch, _) => Task.FromResult<IReadOnlyList<PublishResult>>(
            batch.Select(_ => new PublishResult((PublishStatus)status)).ToList()));
        var dispatcher = DispatcherHarness.Create(dataSource, transport, DispatcherHarness.Options(database));

        await dispatcher.RunOnceAsync(ct);

        Assert.Equal(0, await database.CountAsync("published"));
        Assert.Equal(3, await database.ScalarAsync("SELECT count(*) FROM waybill.outbox WHERE status = 'pending' AND attempts = 0"));
    }

    // ADR 0003: a confirmation timeout or a nack is back-pressure, not an outage. The batch halves and the breaker stays
    // closed, so the next cycle claims right away with the smaller batch.
    [Theory]
    [InlineData(TransportFailure.ConfirmTimeout)]
    [InlineData(TransportFailure.Nacked)]
    public async Task G2_TimeoutDeConfirmacaoOuNack_ReduzLoteSemAbrirBreaker(TransportFailure failure)
    {
        var ct = TestContext.Current.CancellationToken;
        var database = await TestDatabase.CreateAsync(postgres);
        await DispatcherHarness.EnqueueAsync(database, 40);
        await using var dataSource = NpgsqlDataSource.Create(database.ConnectionString);
        var transport = new FakeTransport((call, batch, _) => call <= 2
            ? Task.FromResult<IReadOnlyList<PublishResult>>(batch.Select(_ => PublishResult.RetryAfter(failure, "slow broker")).ToList())
            : FakeTransport.ConfirmAll(batch));
        var dispatcher = DispatcherHarness.Create(dataSource, transport, DispatcherHarness.Options(database, o => o.BatchSize = 20));

        Assert.Equal((20, DispatchOutcome.Pressure), Pair(await dispatcher.RunOnceAsync(ct)));
        Assert.Equal((10, DispatchOutcome.Pressure), Pair(await dispatcher.RunOnceAsync(ct))); // halved, no breaker
        Assert.Equal((5, DispatchOutcome.Progress), Pair(await dispatcher.RunOnceAsync(ct)));
        Assert.Equal((10, DispatchOutcome.Progress), Pair(await dispatcher.RunOnceAsync(ct))); // grows back

        Assert.Equal(15, await database.CountAsync("published"));
        Assert.Equal(0, await database.ScalarAsync("SELECT coalesce(max(attempts), 0) FROM waybill.outbox"));
    }
}
