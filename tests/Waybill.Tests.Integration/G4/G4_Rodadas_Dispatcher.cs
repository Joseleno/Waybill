using Npgsql;
using Waybill.EntityFrameworkCore.Dispatching;
using Waybill.Tests.Integration.G2;

namespace Waybill.Tests.Integration.G4;

// The dispatcher with M ≥ 2 (PLAN.md 8c-2): one call to the transport per round, round k carrying the k-th message of
// each key, and round k + 1 only when every message of round k was confirmed or finished (returned, defective). The
// first copy of each message of a key reaches the broker after the first copy of every earlier one (G4).
[Collection(PostgresCollection.Name)]
public sealed class G4_Rodadas_Dispatcher(PostgresFixture postgres)
{
    // Rounds follow the sequence, not the id: the id comes from the client at enqueue, the sequence at the commit.
    [Fact]
    public async Task G4_RodadasSeguemSequenceNaoId()
    {
        var ct = TestContext.Current.CancellationToken;
        var database = await TestDatabase.CreateAsync(postgres);
        await database.ExecuteAsync("""
            INSERT INTO waybill.outbox (id, type, key, key_hash, sequence, payload, content_type, is_head)
            SELECT ('00000000-0000-7000-8000-' || lpad(to_hex(10 - s), 12, '0'))::uuid, 'billing.invoice-paid.v1', 'K', 0, s,
                   '\x7b7d', 'application/json', s = 1
            FROM generate_series(1, 3) s;
            INSERT INTO waybill.outbox_keys VALUES ('K', 3);
            """); // before ordering is on: with it, the trigger would number them again
        var (_, dispatcher, transport) = await DispatcherAsync(maxPerKey: 3, database);

        var cycle = await dispatcher.RunOnceAsync(ct);

        Assert.Equal([[1L], [2L], [3L]], transport.Batches.Select(b => b.Select(m => m.Sequence!.Value).ToList()));
        Assert.False(cycle.MoreOfKeys); // the whole chain fit
        Assert.Equal(3, await database.CountAsync("published"));
    }

    [Fact]
    public async Task G4_RodadasSeguemSequence_CadeiaMaiorQueM_MaisDaChave()
    {
        var ct = TestContext.Current.CancellationToken;
        var (database, dispatcher, transport) = await DispatcherAsync(maxPerKey: 3);
        await DispatcherHarness.EnqueueAsync(database, 5, key: _ => "K");

        var cycle = await dispatcher.RunOnceAsync(ct);

        Assert.Equal([[1L], [2L], [3L]], transport.Batches.Select(b => b.Select(m => m.Sequence!.Value).ToList()));
        Assert.True(cycle.MoreOfKeys);
    }

    // Rows without a sequence are not ordered: written before ordering was on (with or without a key), they all go in
    // the first round, next to the numbered head of their key; no call carries two numbered messages of one key.
    [Fact]
    public async Task G4_LinhasSemSequence_VaoNaRodadaUm()
    {
        var ct = TestContext.Current.CancellationToken;
        var database = await TestDatabase.CreateAsync(postgres);
        var before = await DispatcherHarness.EnqueueAsync(database, 2, key: _ => "A"); // ordering still off: no sequence
        var loose = await DispatcherHarness.EnqueueAsync(database, 1, key: _ => null);
        var (_, dispatcher, transport) = await DispatcherAsync(maxPerKey: 3, database);
        await DispatcherHarness.EnqueueAsync(database, 3, key: _ => "A");

        await dispatcher.RunOnceAsync(ct);

        var first = transport.Batches.First();
        Assert.Superset(before.Concat(loose).ToHashSet(), first.Select(m => m.MessageId).ToHashSet());
        Assert.Contains(first, m => m.Sequence == 1);
        Assert.All(transport.Batches, b => Assert.True(b.Where(m => m.Sequence is not null).GroupBy(m => m.Key).All(g => g.Count() == 1)));
        Assert.Equal([1L, 2L, 3L], transport.Received.Where(m => m.Sequence is not null).Select(m => m.Sequence!.Value));
    }

    public static TheoryData<string> Failures => ["unspecified", "nacked", "connection", "confirm timeout", "undefined status", "exception", "wrong count"];

    // Any message of a round that has to be published again ends the batch for every key: no round after it. Under
    // pressure or with the connection failing, more publications would only be more duplicates. What was not sent goes
    // back without spending an attempt.
    [Theory]
    [MemberData(nameof(Failures))]
    public async Task G4_RetryNaRodada_EncerraOLote(string failure)
    {
        var ct = TestContext.Current.CancellationToken;
        var (database, dispatcher, transport) = await DispatcherAsync(maxPerKey: 3, behavior: (call, batch, _) =>
        {
            if (failure == "exception")
                throw new InvalidOperationException("the broker went away");
            if (failure == "wrong count")
                return Task.FromResult<IReadOnlyList<PublishResult>>([PublishResult.Confirmed]);
            return Task.FromResult<IReadOnlyList<PublishResult>>(batch.Select(m => m.Key == "B" ? failure switch
            {
                "unspecified" => PublishResult.Retry,
                "nacked" => PublishResult.RetryAfter(TransportFailure.Nacked, "nack"),
                "connection" => PublishResult.RetryAfter(TransportFailure.Connection, "closed"),
                "confirm timeout" => PublishResult.RetryAfter(TransportFailure.ConfirmTimeout, "late"),
                _ => default,
            } : PublishResult.Confirmed).ToList());
        });
        await DispatcherHarness.EnqueueAsync(database, 3, key: _ => "A");
        await DispatcherHarness.EnqueueAsync(database, 3, key: _ => "B");

        await dispatcher.RunOnceAsync(ct);

        Assert.Equal(1, transport.Calls);
        Assert.Equal(0, await database.ScalarAsync("SELECT count(*) FROM waybill.outbox WHERE sequence > 1 AND (status <> 'pending' OR attempts > 0)"));
    }

    // A message returned as unroutable spends an attempt and waits (ADR 0006); the rest of its key's series is not sent
    // and goes back as it was. The other keys go on.
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task G4_RodadaComRetornoDeN_NaoEnviaASeguinte(int round)
    {
        var ct = TestContext.Current.CancellationToken;
        var (database, dispatcher, transport) = await DispatcherAsync(maxPerKey: 3, behavior: (_, batch, _) =>
            Task.FromResult<IReadOnlyList<PublishResult>>(batch.Select(m =>
                m.Key == "A" && m.Sequence == round ? PublishResult.Returned("NO_ROUTE") : PublishResult.Confirmed).ToList()));
        await DispatcherHarness.EnqueueAsync(database, 3, key: _ => "A");
        await DispatcherHarness.EnqueueAsync(database, 3, key: _ => "B");

        await dispatcher.RunOnceAsync(ct);

        Assert.Equal(Enumerable.Range(1, round).Select(s => (long)s), transport.Received.Where(m => m.Key == "A").Select(m => m.Sequence!.Value));
        Assert.Equal([1L, 2L, 3L], transport.Received.Where(m => m.Key == "B").Select(m => m.Sequence!.Value));
        Assert.Equal(1, await database.ScalarAsync($"SELECT attempts FROM waybill.outbox WHERE key = 'A' AND sequence = {round}"));
        Assert.Equal(1, await database.ScalarAsync($"SELECT count(*) FROM waybill.outbox WHERE key = 'A' AND sequence = {round} AND next_attempt_at > clock_timestamp()"));
        Assert.Equal(0, await database.ScalarAsync($"SELECT count(*) FROM waybill.outbox WHERE key = 'A' AND sequence > {round} AND (status <> 'pending' OR attempts > 0)"));
    }

    // A message above MaxPayloadBytes goes to the DLQ in its own round, without touching the broker, and stops its key:
    // the rest of the series is not sent.
    [Fact]
    public async Task G4_DefeitoLocalNoMeioDaSerie_NaoEnviaASeguinte()
    {
        var ct = TestContext.Current.CancellationToken;
        var (database, dispatcher, transport) = await DispatcherAsync(maxPerKey: 3, maxPayloadBytes: 200);
        await DispatcherHarness.EnqueueAsync(database, 3, key: _ => "A");
        await database.ExecuteAsync("UPDATE waybill.outbox SET payload = convert_to(repeat('x', 500), 'UTF8') WHERE key = 'A' AND sequence = 2");
        await DispatcherHarness.EnqueueAsync(database, 3, key: _ => "B");

        await dispatcher.RunOnceAsync(ct);

        Assert.Equal([1L], transport.Received.Where(m => m.Key == "A").Select(m => m.Sequence!.Value));
        Assert.Equal(3, transport.Received.Count(m => m.Key == "B"));
        Assert.Equal(["published", "dlq", "pending"], await StatusesAsync(database, "A"));
    }

    // A message whose round never ran keeps its place: a defect behind a key whose round failed is not sent to the DLQ
    // before its turn.
    [Fact]
    public async Task G4_DefeitoLocalAtrasDeRetry_NaoVaiParaDlqForaDaVez()
    {
        var ct = TestContext.Current.CancellationToken;
        var (database, dispatcher, _) = await DispatcherAsync(maxPerKey: 3, maxPayloadBytes: 200, behavior: (_, batch, _) =>
            Task.FromResult<IReadOnlyList<PublishResult>>(batch.Select(_ => PublishResult.RetryAfter(TransportFailure.Nacked, "nack")).ToList()));
        await DispatcherHarness.EnqueueAsync(database, 2, key: _ => "A");
        await database.ExecuteAsync("UPDATE waybill.outbox SET payload = convert_to(repeat('x', 500), 'UTF8') WHERE key = 'A' AND sequence = 2");

        await dispatcher.RunOnceAsync(ct);

        Assert.Equal(["pending", "pending"], await StatusesAsync(database, "A"));
    }

    public static TheoryData<string> SecondOutcomes => ["confirmed", "returned", "defect", "not sent"];

    // The head and its successor finish in one marking: the key's least unfinished message ends up marked, whatever
    // happened to the successor.
    [Theory]
    [MemberData(nameof(SecondOutcomes))]
    public async Task G4_Ponteiro_CabecaESucessoraNoMesmoLote_PromoveATerceira(string second)
    {
        var ct = TestContext.Current.CancellationToken;
        var (database, dispatcher, _) = await DispatcherAsync(maxPerKey: 3, maxPayloadBytes: 200, behavior: (call, batch, _) =>
            Task.FromResult<IReadOnlyList<PublishResult>>(batch.Select(m => m.Sequence switch
            {
                2 when second == "returned" => PublishResult.Returned("NO_ROUTE"),
                2 when second == "not sent" => PublishResult.RetryAfter(TransportFailure.Nacked, "nack"),
                _ => PublishResult.Confirmed,
            }).ToList()));
        await DispatcherHarness.EnqueueAsync(database, 4, key: _ => "K");
        if (second == "defect")
            await database.ExecuteAsync("UPDATE waybill.outbox SET payload = convert_to(repeat('x', 500), 'UTF8') WHERE key = 'K' AND sequence = 2");

        await dispatcher.RunOnceAsync(ct);

        var least = await database.ScalarAsync("SELECT min(sequence) FROM waybill.outbox WHERE key = 'K' AND status IN ('pending', 'claimed', 'dlq')");
        Assert.Equal(second == "confirmed" ? 4 : 2, least);
        Assert.Equal(1, await database.ScalarAsync($"SELECT count(*) FROM waybill.outbox WHERE key = 'K' AND sequence = {least} AND is_head"));
    }

    // A key stopped by a return or a defect has nothing more to send now: the loop waits the polling interval instead of
    // claiming at once in a circle.
    [Fact]
    public async Task G4_ChaveParadaPorRetorno_LacoNaoGira()
    {
        var ct = TestContext.Current.CancellationToken;
        var (database, dispatcher, _) = await DispatcherAsync(maxPerKey: 3, behavior: (_, batch, _) =>
            Task.FromResult<IReadOnlyList<PublishResult>>(batch.Select(m => m.Sequence == 2 ? PublishResult.Returned("NO_ROUTE") : PublishResult.Confirmed).ToList()));
        await DispatcherHarness.EnqueueAsync(database, 3, key: _ => "K");

        var cycle = await dispatcher.RunOnceAsync(ct);

        Assert.False(cycle.MoreOfKeys);
    }

    // The marking of a batch is repeated when PostgreSQL ends it as a deadlock victim or a serialization failure: its
    // statements are fenced, so running them again changes nothing that already applied (two zombies crossing on two
    // keys can deadlock, found in the 8c-2 review).
    [Theory]
    [InlineData("40P01")]
    [InlineData("40001")]
    public async Task G4_Rodadas_MarcacaoRepetidaEmDeadlock(string sqlState)
    {
        var ct = TestContext.Current.CancellationToken;
        var (database, dispatcher, transport) = await DispatcherAsync(maxPerKey: 3);
        await DispatcherHarness.EnqueueAsync(database, 3, key: _ => "K");
        await database.ExecuteAsync($"""
            CREATE SEQUENCE public.test_fail_once; -- not transactional: survives the failed statement's rollback
            CREATE FUNCTION public.test_fail_once() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN
                IF NEW.status = 'published' AND nextval('public.test_fail_once') = 1 THEN
                    RAISE EXCEPTION 'forced' USING ERRCODE = '{sqlState}';
                END IF;
                RETURN NEW;
            END $$;
            CREATE TRIGGER test_fail_once BEFORE UPDATE ON waybill.outbox FOR EACH ROW EXECUTE FUNCTION public.test_fail_once();
            """);

        await dispatcher.RunOnceAsync(ct);

        Assert.Equal(3, transport.Received.Count);
        Assert.Equal(3, await database.CountAsync("published"));
    }

    private async Task<(TestDatabase, OutboxDispatcher, FakeTransport)> DispatcherAsync(
        int maxPerKey, TestDatabase? existing = null, int maxPayloadBytes = 64 * 1024,
        Func<int, IReadOnlyList<OutgoingMessage>, CancellationToken, Task<IReadOnlyList<PublishResult>>>? behavior = null)
    {
        var database = existing ?? await TestDatabase.CreateAsync(postgres);
        var dataSource = NpgsqlDataSource.Create(database.ConnectionString);
        var transport = new FakeTransport(behavior);
        var options = OrderingHarness.Options(database, partitions: 4, maxPerKey: maxPerKey);
        options.ReturnBackoff = TimeSpan.FromMinutes(1);
        var dispatcher = DispatcherHarness.Create(dataSource, transport, options, maxPayloadBytes, orderByKey: true);
        Assert.Null(await dispatcher.CheckOrderingAsync(atStartup: true, TestContext.Current.CancellationToken));
        if (existing is null) // a seeded database: the test's own cycle takes the partitions and claims at once
            Assert.Equal(0, (await dispatcher.RunOnceAsync(TestContext.Current.CancellationToken)).Claimed); // takes the partitions
        return (database, dispatcher, transport);
    }

    private static async Task<List<string>> StatusesAsync(TestDatabase database, string key)
    {
        await using var connection = new NpgsqlConnection(database.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand($"SELECT status FROM waybill.outbox WHERE key = '{key}' ORDER BY sequence", connection);
        var statuses = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            statuses.Add(reader.GetString(0));
        return statuses;
    }
}
