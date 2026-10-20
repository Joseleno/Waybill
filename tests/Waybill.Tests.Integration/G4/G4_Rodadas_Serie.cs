using Npgsql;
using Waybill.EntityFrameworkCore.Dispatching;
using Waybill.Tests.Integration.G2;

namespace Waybill.Tests.Integration.G4;

// What the series claim (M ≥ 2) takes of a key, row by row (PLAN.md 8c-2): its head, then the rows right after it,
// stopping at the first one it cannot take, and never taking a row whose predecessor in the key stays out.
[Collection(PostgresCollection.Name)]
public sealed class G4_Rodadas_Serie(PostgresFixture postgres)
{
    private const string Owner = "instance";
    private static readonly TimeSpan Lease = TimeSpan.FromSeconds(30);

    public static TheoryData<string, string> MiddleStates => new()
    {
        { "another owner's live lease", "UPDATE waybill.outbox SET status = 'claimed', owner = 'other', fence = fence + 1, lease_until = clock_timestamp() + interval '1 hour'" },
        { "a wait after basic.return", "UPDATE waybill.outbox SET next_attempt_at = clock_timestamp() + interval '1 hour'" },
        { "the DLQ", "UPDATE waybill.outbox SET status = 'dlq', dlq_reason = 'test'" },
    };

    // The row after the head cannot be taken, by its state in the claim's snapshot: nothing after it either.
    [Theory]
    [MemberData(nameof(MiddleStates))]
    public async Task G4_AntecessoraDoMeio_PrefixoCorta(string why, string setS2)
    {
        var ct = TestContext.Current.CancellationToken;
        var (database, store) = await KeyAsync(rows: 3);
        await database.ExecuteAsync(setS2 + " WHERE key = 'K' AND sequence = 2");

        var claimed = await store.ClaimAsync(Owner, 10, Lease, ct, partitions: 4, maxPerKey: 3);

        Assert.Equal([1L], claimed.Select(c => c.Sequence!.Value));
        Assert.False(string.IsNullOrEmpty(why));
    }

    // The head itself waits after a basic.return (ADR 0006): nothing of its key goes. Taken as a head only to be dropped
    // by the UPDATE, it would have brought its successors along without it.
    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    public async Task G4_CabecaEmEspera_NadaDaChaveSai(int maxPerKey)
    {
        var ct = TestContext.Current.CancellationToken;
        var (database, store) = await KeyAsync(rows: 3, keys: ["K", "L"]);
        await database.ExecuteAsync("UPDATE waybill.outbox SET next_attempt_at = clock_timestamp() + interval '1 hour', attempts = 1 WHERE key = 'K' AND sequence = 1");

        var claimed = await store.ClaimAsync(Owner, 10, Lease, ct, partitions: 4, maxPerKey: maxPerKey);

        Assert.DoesNotContain(claimed, c => c.Message.Key == "K");
        Assert.Equal(maxPerKey, claimed.Count(c => c.Message.Key == "L"));
    }

    // Locked by another session (a concurrent mark, a zombie's late hand-back): SKIP LOCKED passes over it, and the claim
    // must not take what comes after it.
    [Fact]
    public async Task G4_AntecessoraDoMeio_Travada_PrefixoCorta()
    {
        var ct = TestContext.Current.CancellationToken;
        var (database, store) = await KeyAsync(rows: 3);
        await using var holder = new NpgsqlConnection(database.ConnectionString);
        await holder.OpenAsync(ct);
        await using var transaction = await holder.BeginTransactionAsync(ct);
        await KeyLockHarness.ExecuteAsync(holder, "SELECT 1 FROM waybill.outbox WHERE key = 'K' AND sequence = 2 FOR UPDATE");

        var claimed = await store.ClaimAsync(Owner, 10, Lease, ct, partitions: 4, maxPerKey: 3);

        Assert.Equal([1L], claimed.Select(c => c.Sequence!.Value));
    }

    public static TheoryData<string, string> ChangesAfterTheSnapshot => new()
    {
        { "to the DLQ (a zombie)", "UPDATE waybill.outbox SET status = 'dlq', dlq_reason = 'test'" },
        { "published (a zombie)", "UPDATE waybill.outbox SET status = 'published', published_at = clock_timestamp()" },
        { "deleted", "DELETE FROM waybill.outbox" },
        { "waiting after basic.return (a zombie)", "UPDATE waybill.outbox SET next_attempt_at = clock_timestamp() + interval '1 hour'" },
    };

    // The row after the head changes and commits after the claim's snapshot, before its lock (forced with the test clock):
    // the snapshot says it can go, the locked row says it cannot. The claim re-checks the locked row, and the series stops.
    [Theory]
    [MemberData(nameof(ChangesAfterTheSnapshot))]
    public async Task G4_SucessoraMudaDepoisDoSnapshot_PrefixoCorta(string why, string changeS2)
    {
        var ct = TestContext.Current.CancellationToken;
        var (database, _) = await KeyAsync(rows: 3);
        await using var clock = await TestClock.InstallAsync(database);
        var store = new OutboxStore(clock.DataSource);

        await clock.PauseAsync();
        var claim = store.ClaimAsync(Owner, 10, Lease, ct, partitions: 4, maxPerKey: 3);
        await clock.WaitForPausedAsync();
        await database.ExecuteAsync(changeS2 + " WHERE key = 'K' AND sequence = 2");
        await clock.ResumeAsync();

        Assert.Equal([1L], (await claim).Select(c => c.Sequence!.Value));
        Assert.Equal(0, await database.ScalarAsync("SELECT count(*) FROM waybill.outbox WHERE key = 'K' AND sequence = 3 AND status <> 'pending'"));
        Assert.False(string.IsNullOrEmpty(why));
    }

    // A row the snapshot already rules out ends its key's series there, without taking a place in the batch: the place
    // goes to another key's series. Five places for two heads: each series may take up to two (its share of what the heads
    // left, rounded up); A's second row cannot go, so B takes both of its own. Judged only at the lock, A2 would take B3's place.
    [Fact]
    public async Task G4_SucessoraNaoReivindicavel_NaoGastaVaga()
    {
        var ct = TestContext.Current.CancellationToken;
        var (database, store) = await KeyAsync(rows: 3, keys: ["A", "B"]);
        await database.ExecuteAsync("""
            UPDATE waybill.outbox SET status = 'claimed', owner = 'other', fence = fence + 1, lease_until = clock_timestamp() + interval '1 hour'
            WHERE key = 'A' AND sequence = 2
            """);

        var claimed = await store.ClaimAsync(Owner, 5, Lease, ct, partitions: 4, maxPerKey: 3);

        Assert.Equal(["A1", "B1", "B2", "B3"], claimed.Select(c => $"{c.Message.Key}{c.Sequence}").Order());
    }

    // A batch the heads fill leaves no place for a successor, and the claim locks none: a successor locked and left out
    // would be held from every other claim until the statement ends, for nothing.
    [Theory]
    [InlineData(2)]
    [InlineData(100)]
    public async Task G4_LoteCheioDeCabecas_MNaoAge(int maxPerKey)
    {
        var ct = TestContext.Current.CancellationToken;
        var (database, store) = await KeyAsync(rows: 3, keys: ["A", "B", "C", "D", "E"]);

        var claimed = await store.ClaimAsync(Owner, 5, Lease, ct, partitions: 4, maxPerKey: maxPerKey);

        Assert.All(claimed, c => Assert.Equal(1L, c.Sequence));
        Assert.Equal(5, claimed.Count);
        Assert.Equal(0, await database.ScalarAsync("SELECT count(*) FROM waybill.outbox WHERE sequence > 1 AND xmax <> 0"));
    }

    // Cut by round: every key's second row before any key's third, so the batch is shared between keys and no series is
    // cut in its middle. Seven places, three heads: four left, up to two per series (A2, A3, B2, B3, C2, C3); by round
    // they go to A2, B2, C2 and A3, where a cut by key would give A2, A3, B2, B3 and leave C with its head alone.
    [Fact]
    public async Task G4_CorteDoLote_RodadaAntesDaChave()
    {
        var ct = TestContext.Current.CancellationToken;
        var (_, store) = await KeyAsync(rows: 5, keys: ["A", "B", "C"]);

        var claimed = await store.ClaimAsync(Owner, 7, Lease, ct, partitions: 4, maxPerKey: 5);

        Assert.Equal(["A1", "A2", "A3", "B1", "B2", "C1", "C2"], claimed.Select(c => $"{c.Message.Key}{c.Sequence}").Order());
        Assert.Equal([0, 0, 0, 1, 1, 1, 2], claimed.Select(c => c.Rank));
    }

    // The id comes from the client at enqueue, the sequence at the commit: a successor can have a smaller id than its head.
    // A batch of one takes the head; a batch of two takes both, the head first.
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task G4_SucessoraComIdMenor_LoteDeUm_CabecaSai(int batch)
    {
        var ct = TestContext.Current.CancellationToken;
        var database = await TestDatabase.CreateAsync(postgres);
        await database.ExecuteAsync("""
            INSERT INTO waybill.outbox (id, type, key, key_hash, sequence, payload, content_type, is_head) VALUES
                ('00000000-0000-7000-8000-000000000009', 'billing.invoice-paid.v1', 'K', 0, 1, '\x7b7d', 'application/json', true),
                ('00000000-0000-7000-8000-000000000001', 'billing.invoice-paid.v1', 'K', 0, 2, '\x7b7d', 'application/json', false);
            INSERT INTO waybill.outbox_keys VALUES ('K', 2);
            """);
        var store = await G4_Rodadas_Claim.OwnAllPartitionsAsync(database);

        var claimed = await store.ClaimAsync(Owner, batch, Lease, ct, partitions: 4, maxPerKey: 2);

        Assert.Equal(batch == 1 ? new long[] { 1 } : new long[] { 1, 2 }, claimed.Select(c => c.Sequence!.Value));
        Assert.Equal(batch == 1 ? new[] { 0 } : new[] { 0, 1 }, claimed.Select(c => c.Rank));
    }

    // M larger than the key's chain takes the whole chain; a batch shorter than the chain cuts it, from its end.
    [Theory]
    [InlineData(10, 10, new long[] { 1, 2, 3 })]
    [InlineData(2, 10, new long[] { 1, 2 })]
    public async Task G4_MaiorQueACadeiaELoteCurto_PrefixoPorChave(int batch, int maxPerKey, long[] expected)
    {
        var ct = TestContext.Current.CancellationToken;
        var (_, store) = await KeyAsync(rows: 3);

        var claimed = await store.ClaimAsync(Owner, batch, Lease, ct, partitions: 4, maxPerKey: maxPerKey);

        Assert.Equal(expected, claimed.Select(c => c.Sequence!.Value));
    }

    // A successor wrongly marked (a false positive: a producer in REPEATABLE READ, ordering turned off and on) is not a
    // head: it comes once, in its own round.
    [Fact]
    public async Task G4_MarcaFalsoPositiva_SucessoraUmaVezNaRodadaCerta()
    {
        var ct = TestContext.Current.CancellationToken;
        var (database, store) = await KeyAsync(rows: 3);
        await database.ExecuteAsync("UPDATE waybill.outbox SET is_head = true WHERE key = 'K' AND sequence = 2");

        var claimed = await store.ClaimAsync(Owner, 10, Lease, ct, partitions: 4, maxPerKey: 3);

        Assert.Equal([(1L, 0), (2L, 1), (3L, 2)], claimed.Select(c => (c.Sequence!.Value, c.Rank)));
    }

    // A released row is finished: the series passes over its gap.
    [Fact]
    public async Task G4_SerieComLiberadaNoMeio_SerieContinua()
    {
        var ct = TestContext.Current.CancellationToken;
        var (database, store) = await KeyAsync(rows: 3);
        await database.ExecuteAsync("""
            UPDATE waybill.outbox SET status = 'released', released_at = clock_timestamp(), released_by = 'ops'
            WHERE key = 'K' AND sequence = 2
            """);

        var claimed = await store.ClaimAsync(Owner, 10, Lease, ct, partitions: 4, maxPerKey: 3);

        Assert.Equal([(1L, 0), (3L, 1)], claimed.Select(c => (c.Sequence!.Value, c.Rank)));
    }

    /// <summary>Ordering on, the partitions held by <see cref="Owner"/>, and <paramref name="rows"/> rows of each key written in one transaction.</summary>
    private async Task<(TestDatabase, OutboxStore)> KeyAsync(int rows, string[]? keys = null)
    {
        var database = await TestDatabase.CreateAsync(postgres);
        var store = await G4_Rodadas_Claim.OwnAllPartitionsAsync(database);
        foreach (var key in keys ?? ["K"])
            await DispatcherHarness.EnqueueAsync(database, rows, key: _ => key);
        return (database, store);
    }
}
