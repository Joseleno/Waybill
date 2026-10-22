using Microsoft.Extensions.Time.Testing;
using Npgsql;
using Waybill.EntityFrameworkCore.Dispatching;
using Waybill.Tests.Integration.G2;

namespace Waybill.Tests.Integration.G4;

// The rounds' deadline and how the dispatcher reacts to them (PLAN.md 8c-2, ADR 0003). The whole batch shares one
// PublishTimeout, counted from before the claim: the lease (PublishTimeout + LeaseMargin) must outlive every round and
// the marking. A round starts only if what is left is at least what the previous round took; a round not started sends
// nothing and spends nothing. The transport here moves a fake clock as if the broker took that long to confirm.
[Collection(PostgresCollection.Name)]
public sealed class G4_Rodadas_Prazo(PostgresFixture postgres)
{
    private static readonly TimeSpan PublishTimeout = TimeSpan.FromSeconds(20);

    // Round 1 took 15 s of 20: 5 s left, less than round 1 took. Round 2 does not start; its messages go back as they
    // were, and the batch size does not shrink, since the broker never failed.
    [Fact]
    public async Task G4_RodadaTardia_NaoComeca_DevolveSemTentativaELoteNaoCai()
    {
        var ct = TestContext.Current.CancellationToken;
        var time = new FakeTimeProvider();
        var (database, dispatcher, transport) = await DispatcherAsync(time, maxPerKey: 3, (call, batch) =>
        {
            time.Advance(TimeSpan.FromSeconds(15));
            return Confirm(batch);
        });
        await DispatcherHarness.EnqueueAsync(database, 3, key: _ => "K");

        var cycle = await dispatcher.RunOnceAsync(ct);

        Assert.Equal(1, transport.Calls);
        Assert.Equal(0, await database.ScalarAsync("SELECT count(*) FROM waybill.outbox WHERE sequence > 1 AND (status <> 'pending' OR attempts > 0)"));
        Assert.Equal(new WaybillDispatcherOptions().BatchSize, dispatcher.CurrentBatchSize);
        Assert.Equal(DispatchOutcome.Progress, cycle.Outcome);
        Assert.True(cycle.MoreOfKeys); // what went back is claimable at once: no polling interval
    }

    // Rounds that keep running out of time: M comes down to the rounds that fit, so the next batches stop claiming
    // successors only to hand them back. Once a batch runs whole, M doubles back towards MaxPerKey.
    [Fact]
    public async Task G4_RodadaCortadaPeloPrazo_MEfetivoCai_EVoltaAoTerminarInteiro()
    {
        var ct = TestContext.Current.CancellationToken;
        var time = new FakeTimeProvider();
        var slow = true;
        var logs = new LogSink();
        var (database, dispatcher, transport) = await DispatcherAsync(time, maxPerKey: 5, (call, batch) =>
        {
            if (slow)
                time.Advance(TimeSpan.FromSeconds(6)); // 6 s a round: three fit in 20 s, a fourth would not
            return Confirm(batch);
        }, logs);
        await DispatcherHarness.EnqueueAsync(database, 5, key: _ => "K");

        var cycle = await dispatcher.RunOnceAsync(ct);

        Assert.Equal(3, transport.Calls);
        Assert.Equal(3, dispatcher.CurrentMaxPerKey);
        Assert.True(cycle.MoreOfKeys);
        Assert.Contains(logs.Entries, e => e.Message.Contains("MaxPerKey") && e.Message.Contains('3'));

        slow = false;
        await DispatcherHarness.EnqueueAsync(database, 1, key: _ => "K");
        await dispatcher.RunOnceAsync(ct); // 4, 5 and 6 of the key: three rounds, all of them

        Assert.Equal(5, dispatcher.CurrentMaxPerKey);
        Assert.Equal(6, await database.CountAsync("published"));
    }

    // Round 2 holds only a message of a key that round 1 stopped, so it sends nothing and takes no time: it is not cut by
    // the deadline, and M does not come down for it.
    [Fact]
    public async Task G4_RodadaSemNadaAEnviar_NaoBaixaOMEfetivo()
    {
        var ct = TestContext.Current.CancellationToken;
        var time = new FakeTimeProvider();
        var (database, dispatcher, transport) = await DispatcherAsync(time, maxPerKey: 3, (call, batch) =>
        {
            time.Advance(TimeSpan.FromSeconds(15)); // 5 s left: a round that sends would not start
            return Task.FromResult<IReadOnlyList<PublishResult>>(
                batch.Select(m => m.Key == "B" ? PublishResult.Returned("NO_ROUTE") : PublishResult.Confirmed).ToList());
        });
        await DispatcherHarness.EnqueueAsync(database, 3, key: i => i < 2 ? "B" : "A");

        await dispatcher.RunOnceAsync(ct);

        Assert.Equal(1, transport.Calls);
        Assert.Equal(3, dispatcher.CurrentMaxPerKey);
    }

    // A later round has only what is left of the deadline as its confirmation timeout, not a whole PublishTimeout. That
    // timeout runs on the real clock: round 1 takes 3 s of a 6 s deadline on the fake one, and round 2, which the broker
    // never confirms, is given up after about 3 s of real time instead of 6.
    [Fact]
    public async Task G4_RodadaTardia_TemSoOPrazoRestante()
    {
        var ct = TestContext.Current.CancellationToken;
        var time = new FakeTimeProvider();
        var (database, dispatcher, transport) = await DispatcherAsync(time, maxPerKey: 2, (call, batch) =>
        {
            if (call == 2)
                return new TaskCompletionSource<IReadOnlyList<PublishResult>>().Task; // never confirmed
            time.Advance(TimeSpan.FromSeconds(3));
            return Confirm(batch);
        }, publishTimeout: TimeSpan.FromSeconds(6));
        await DispatcherHarness.EnqueueAsync(database, 2, key: _ => "K");

        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        await dispatcher.RunOnceAsync(ct);
        elapsed.Stop();

        Assert.Equal(2, transport.Calls);
        Assert.InRange(elapsed.Elapsed, TimeSpan.FromSeconds(2.5), TimeSpan.FromSeconds(5));
    }

    public static TheoryData<string, int, bool> Pressure => new()
    {
        { "confirm timeout", 2, false }, // round 2 had only what round 1 left of the deadline
        { "nacked", 2, true },           // the broker pushing back does not depend on the deadline
        { "confirm timeout", 1, true },  // control: round 1 had the whole PublishTimeout
    };

    [Theory]
    [MemberData(nameof(Pressure))]
    public async Task G4_FalhaNaRodada_PressaoSoQuandoNaoVemDoPrazo(string failure, int round, bool halves)
    {
        var ct = TestContext.Current.CancellationToken;
        var (database, dispatcher, _) = await DispatcherAsync(new FakeTimeProvider(), maxPerKey: 3, (call, batch) =>
            call == round
                ? Task.FromResult<IReadOnlyList<PublishResult>>(batch.Select(_ => PublishResult.RetryAfter(
                    failure == "nacked" ? TransportFailure.Nacked : TransportFailure.ConfirmTimeout, failure)).ToList())
                : Confirm(batch));
        await DispatcherHarness.EnqueueAsync(database, 3, key: _ => "K");
        var before = dispatcher.CurrentBatchSize;

        await dispatcher.RunOnceAsync(ct);

        Assert.Equal(halves ? before / 2 : before, dispatcher.CurrentBatchSize);
    }

    [Fact]
    public async Task G4_ConexaoNaRodadaDois_AbreOBreaker()
    {
        var ct = TestContext.Current.CancellationToken;
        var (database, dispatcher, _) = await DispatcherAsync(new FakeTimeProvider(), maxPerKey: 3, (call, batch) =>
            call == 2
                ? Task.FromResult<IReadOnlyList<PublishResult>>(batch.Select(_ => PublishResult.RetryAfter(TransportFailure.Connection, "closed")).ToList())
                : Confirm(batch));
        await DispatcherHarness.EnqueueAsync(database, 3, key: _ => "K");

        Assert.Equal(DispatchOutcome.ConnectionFailure, (await dispatcher.RunOnceAsync(ct)).Outcome);
        Assert.True(dispatcher.BreakerRemaining > TimeSpan.Zero);
    }

    // Half-open, the breaker probes with one message: M does not turn that into a series.
    [Fact]
    public async Task G4_MeioAberto_ComMCem_SondaUmaSo()
    {
        var ct = TestContext.Current.CancellationToken;
        var time = new FakeTimeProvider();
        var failing = true;
        var (database, dispatcher, transport) = await DispatcherAsync(time, maxPerKey: 100, (call, batch) => failing
            ? Task.FromResult<IReadOnlyList<PublishResult>>(batch.Select(_ => PublishResult.RetryAfter(TransportFailure.Connection, "closed")).ToList())
            : Confirm(batch));
        await DispatcherHarness.EnqueueAsync(database, 10, key: _ => "K");
        Assert.Equal(DispatchOutcome.ConnectionFailure, (await dispatcher.RunOnceAsync(ct)).Outcome);
        time.Advance(dispatcher.BreakerRemaining + TimeSpan.FromMilliseconds(1));
        failing = false;
        var before = transport.Received.Count;

        await dispatcher.RunOnceAsync(ct);

        Assert.Equal(1, transport.Received.Count - before);
    }

    private static Task<IReadOnlyList<PublishResult>> Confirm(IReadOnlyList<OutgoingMessage> batch) => FakeTransport.ConfirmAll(batch);

    private async Task<(TestDatabase, OutboxDispatcher, FakeTransport)> DispatcherAsync(
        FakeTimeProvider time, int maxPerKey, Func<int, IReadOnlyList<OutgoingMessage>, Task<IReadOnlyList<PublishResult>>> behavior, LogSink? logs = null,
        TimeSpan? publishTimeout = null)
    {
        var database = await TestDatabase.CreateAsync(postgres);
        var dataSource = NpgsqlDataSource.Create(database.ConnectionString);
        var transport = new FakeTransport((call, batch, _) => behavior(call, batch));
        var options = OrderingHarness.Options(database, partitions: 4, maxPerKey: maxPerKey);
        options.PublishTimeout = publishTimeout ?? PublishTimeout;
        options.PartitionLease = TimeSpan.FromDays(1); // the fake clock jumps; the partitions must not expire in it
        var dispatcher = OrderingHarness.Create(dataSource, transport, options, time, logs);
        Assert.Null(await dispatcher.CheckOrderingAsync(atStartup: true, TestContext.Current.CancellationToken));
        Assert.Equal(0, (await dispatcher.RunOnceAsync(TestContext.Current.CancellationToken)).Claimed); // takes the partitions
        return (database, dispatcher, transport);
    }
}
