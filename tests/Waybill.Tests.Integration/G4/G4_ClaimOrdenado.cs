using Npgsql;
using Waybill.EntityFrameworkCore.Dispatching;
using Waybill.Tests.Integration.G2;

namespace Waybill.Tests.Integration.G4;

// The ordered claim (ADR 0008, M = 1): of each key, only its head, the first row not yet published or released.
[Collection(PostgresCollection.Name)]
public sealed class G4_ClaimOrdenado(PostgresFixture postgres)
{
    [Fact]
    public async Task G4_CabecaPorChave_UmaPorChavePorLote_EmOrdem()
    {
        var ct = TestContext.Current.CancellationToken;
        var database = await TestDatabase.CreateAsync(postgres);
        await using var dataSource = NpgsqlDataSource.Create(database.ConnectionString);
        var transport = new FakeTransport();
        var dispatcher = OrderingHarness.Create(dataSource, transport, OrderingHarness.Options(database, partitions: 4));
        Assert.Null(await dispatcher.CheckOrderingAsync(atStartup: true, ct)); // ordering on: keyed rows are numbered from now
        await DispatcherHarness.EnqueueAsync(database, 9, key: i => "ABC"[i % 3].ToString());

        for (var round = 1; round <= 3; round++)
        {
            var before = transport.Received.Count;
            var cycle = await dispatcher.RunOnceAsync(ct);
            var batch = transport.Received.Skip(before).ToList();
            Assert.Equal(3, cycle.Claimed);
            Assert.Equal(round < 3, cycle.MoreOfKeys);
            Assert.Equal(["A", "B", "C"], batch.Select(m => m.Key!).Order());
            Assert.All(await SequencesOfAsync(database, batch), s => Assert.Equal(round, s));
        }
        Assert.Equal(0, (await dispatcher.RunOnceAsync(ct)).Claimed);
    }

    // The spike's open risk (ADR 0001): SKIP LOCKED passes over a head locked by a concurrent mark or hand-back and, with
    // more than one row per key, takes the next. Here the head is claimable (its zombie's lease ran out) but locked by
    // that zombie's late statement when the claim runs: nothing of the key is taken. Once it commits, the mark lets the
    // next row out; the hand-back lets the head out first.
    [Theory]
    [InlineData("published")]
    [InlineData("pending")]
    public async Task G4_MarcacaoOuDevolucaoConcorrente_ClaimNaoLevaASeguinte(string zombieWrites)
    {
        var ct = TestContext.Current.CancellationToken;
        var database = await TestDatabase.CreateAsync(postgres);
        await using var dataSource = NpgsqlDataSource.Create(database.ConnectionString);
        var transport = new FakeTransport();
        var dispatcher = OrderingHarness.Create(dataSource, transport, OrderingHarness.Options(database, partitions: 4));
        Assert.Null(await dispatcher.CheckOrderingAsync(atStartup: true, ct));
        await DispatcherHarness.EnqueueAsync(database, 2, key: _ => "K");
        await database.ExecuteAsync("""
            UPDATE waybill.outbox SET status = 'claimed', owner = 'zombie', fence = fence + 1, lease_until = clock_timestamp() - interval '1 second'
            WHERE key = 'K' AND sequence = 1
            """);

        await using var zombie = new NpgsqlConnection(database.ConnectionString);
        await zombie.OpenAsync(ct);
        await using var late = await zombie.BeginTransactionAsync(ct);
        await KeyLockHarness.ExecuteAsync(zombie, $"""
            UPDATE waybill.outbox SET status = '{zombieWrites}', lease_until = NULL WHERE key = 'K' AND sequence = 1
            """);

        await dispatcher.RunOnceAsync(ct);
        Assert.Empty(transport.Received);

        await late.CommitAsync(ct);
        await dispatcher.RunOnceAsync(ct);
        Assert.Equal([zombieWrites == "published" ? 2L : 1L], await SequencesOfAsync(database, transport.Received.ToList()));
    }

    [Fact]
    public async Task G4_DispatcherSemOrdenacaoAindaVivo_NaoReivindicaLinhaComChave()
    {
        var ct = TestContext.Current.CancellationToken;
        var database = await TestDatabase.CreateAsync(postgres);
        await KeyLockHarness.EnableOrderingAsync(database); // another dispatcher orders
        await DispatcherHarness.EnqueueAsync(database, 10, key: i => i % 2 == 0 ? null : $"invoice-{i}");
        await using var dataSource = NpgsqlDataSource.Create(database.ConnectionString);
        var transport = new FakeTransport();
        var stale = DispatcherHarness.Create(dataSource, transport, DispatcherHarness.Options(database)); // not ordering, not checked yet

        await stale.RunOnceAsync(ct);

        Assert.Equal(5, transport.Received.Count);
        Assert.All(transport.Received, m => Assert.Null(m.Key));
    }

    // The clock is read once: every row of the claim, and the lease it takes, see the same instant.
    [Fact]
    public void G4_ClaimOrdenado_UmInstantePorInstrucao() =>
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(OutboxStore.ClaimOrderedSql, @"clock_timestamp\(\)"));

    private static async Task<List<long>> SequencesOfAsync(TestDatabase database, IReadOnlyList<OutgoingMessage> messages)
    {
        var sequences = new List<long>();
        foreach (var message in messages)
            sequences.Add(await database.ScalarAsync($"SELECT sequence FROM waybill.outbox WHERE id = '{message.MessageId}'"));
        return sequences;
    }
}
