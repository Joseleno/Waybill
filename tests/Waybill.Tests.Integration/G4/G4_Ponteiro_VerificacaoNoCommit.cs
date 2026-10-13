using Npgsql;
using Waybill.EntityFrameworkCore.Dispatching;
using Waybill.Tests.Integration.G2;

namespace Waybill.Tests.Integration.G4;

// The head pointer's race (PLAN.md, "Ponteiro de cabeça"): a producer inserting behind a head the dispatcher is finishing
// cannot see that publication if it has not committed, and the publication's promotion cannot see the producer's row.
// The producer decides again at its commit: it locks its predecessor FOR SHARE SKIP LOCKED. If it locked first, the
// dispatcher's update waits for the end of that commit, and the promotion then sees the row; if the dispatcher locked
// first, the producer skips the predecessor and marks itself. The producer never waits for the dispatcher. Every
// interleaving is forced with a barrier, never left to chance.
[Collection(PostgresCollection.Name)]
public sealed class G4_Ponteiro_VerificacaoNoCommit(PostgresFixture postgres)
{
    private const string App = "app";

    [Fact]
    public async Task G4_Ponteiro_PublicacaoAntesDoCommitDoProdutor_SucessoraViraCabeca()
    {
        var s = await Scenario.StartAsync(postgres, "K");
        await using var barrier = await Barrier.InstallAsync(s.Database, "waybill.outbox", "INSERT");
        var producer = KeyLockHarness.EnqueueAsync(s.Database, KeyLockHarness.BarrierApp, "K");
        await barrier.WaitForAsync(atBarrier: 1, onLocks: 0);

        await s.PublishAsync(s.Heads["K"]); // committed; its promotion cannot see the producer's row
        await barrier.ReleaseAsync();
        await producer;

        Assert.Equal("2:pending:true", await s.UnfinishedAsync("K"));
        Assert.Equal(new long[] { 2 }, await s.ClaimSequencesAsync("K"));
    }

    [Fact]
    public async Task G4_Ponteiro_MarcacaoEsperaOCommitDoProdutor_PromocaoVeASucessora()
    {
        var s = await Scenario.StartAsync(postgres, "K");
        await using var barrier = await Barrier.InstallAsync(s.Database, "waybill.outbox", "INSERT", atCommit: true);
        var producer = KeyLockHarness.EnqueueAsync(s.Database, KeyLockHarness.BarrierApp, "K");
        await barrier.WaitForAsync(atBarrier: 1, onLocks: 0); // in its commit, the head locked FOR SHARE

        var marking = s.PublishAsync(s.Heads["K"]);
        await barrier.WaitForAsync(atBarrier: 1, onLocks: 1); // the dispatcher's update waits for the producer's commit
        await barrier.ReleaseAsync();
        await producer;
        await marking;

        Assert.Equal("2:pending:true", await s.UnfinishedAsync("K")); // marked by the promotion, which saw the row
        Assert.Equal(new long[] { 2 }, await s.ClaimSequencesAsync("K"));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task G4_Ponteiro_MarcacaoEmVooQuandoProdutorCommita_FalsoPositivoInofensivo(bool dispatcherCommits)
    {
        var s = await Scenario.StartAsync(postgres, "K");
        await using var dispatcher = new NpgsqlConnection(s.Database.ConnectionString);
        await dispatcher.OpenAsync(TestContext.Current.CancellationToken);
        await using var marking = await dispatcher.BeginTransactionAsync(TestContext.Current.CancellationToken);
        await KeyLockHarness.ExecuteAsync(dispatcher, Scenario.PublishSql(s.Heads["K"]));

        await KeyLockHarness.EnqueueAsync(s.Database, App, "K"); // commits without waiting: skips the locked head
        Assert.Equal("1:published:true,2:pending:true", await s.UnfinishedAsync("K", connection: dispatcher));

        if (dispatcherCommits)
        {
            await marking.CommitAsync(TestContext.Current.CancellationToken);
            Assert.Equal(new long[] { 2 }, await s.ClaimSequencesAsync("K"));
        }
        else
        {
            // The publication undone: the producer's mark is a false positive, and the claim still takes the head.
            await marking.RollbackAsync(TestContext.Current.CancellationToken);
            await s.Store.ReleaseOwnedAsync(s.Owner, TestContext.Current.CancellationToken);
            Assert.Equal("1:pending:true,2:pending:true", await s.UnfinishedAsync("K"));
            Assert.Equal(new long[] { 1 }, await s.ClaimSequencesAsync("K"));
        }
    }

    [Fact]
    public async Task G4_Ponteiro_ProdutorDeDuasChavesContraMarcacaoEmLote_SemDeadlock()
    {
        var s = await Scenario.StartAsync(postgres, "A", "B");
        await using var barrier = await Barrier.InstallAsync(s.Database, "waybill.outbox", "INSERT", atCommit: true);
        await using var dispatcher = new NpgsqlConnection(s.Database.ConnectionString);
        await dispatcher.OpenAsync(TestContext.Current.CancellationToken);
        await using var marking = await dispatcher.BeginTransactionAsync(TestContext.Current.CancellationToken);
        await KeyLockHarness.ExecuteAsync(dispatcher, Scenario.PublishSql(s.Heads["B"])); // the batch holds B's head

        var producer = KeyLockHarness.EnqueueAsync(s.Database, KeyLockHarness.BarrierApp, "A", "B");
        await barrier.WaitForAsync(atBarrier: 1, onLocks: 0); // A's check done (its head locked FOR SHARE), B's not yet
        var batch = Task.Run(async () =>
        {
            await KeyLockHarness.ExecuteAsync(dispatcher, Scenario.PublishSql(s.Heads["A"])); // waits for the producer
            await marking.CommitAsync(TestContext.Current.CancellationToken);
        }, TestContext.Current.CancellationToken);
        await barrier.WaitForAsync(atBarrier: 1, onLocks: 1);
        await barrier.ReleaseAsync();

        await producer; // B's head is locked by the batch: skipped, not waited for
        await batch;
        Assert.Equal("2:pending:true", await s.UnfinishedAsync("A"));
        Assert.Equal("2:pending:true", await s.UnfinishedAsync("B"));
    }

    [Fact]
    public async Task G4_Ponteiro_LiberacaoConcorrente_SucessoraViraCabeca()
    {
        var s = await Scenario.StartAsync(postgres, "K");
        await s.Database.ExecuteAsync($"UPDATE waybill.outbox SET status = 'dlq', dlq_reason = 'defect', lease_until = NULL WHERE id = '{s.Heads["K"]}'");
        await using var barrier = await Barrier.InstallAsync(s.Database, "waybill.outbox", "INSERT");
        var producer = KeyLockHarness.EnqueueAsync(s.Database, KeyLockHarness.BarrierApp, "K");
        await barrier.WaitForAsync(atBarrier: 1, onLocks: 0);

        await s.Database.ExecuteAsync(OperationsDoc.Sql("key-release").Replace("order-42", "K"));
        await barrier.ReleaseAsync();
        await producer;

        Assert.Equal("2:pending:true", await s.UnfinishedAsync("K"));
        Assert.Equal(new long[] { 2 }, await s.ClaimSequencesAsync("K"));
    }

    // A burst of one key in one transaction, behind another transaction's head: only its first row is checked at the
    // commit (and stays undecided, the head being unfinished); the rest are known to sit behind it.
    [Fact]
    public async Task G4_Ponteiro_RajadaNaTransacao_SoAPrimeiraDecideNoCommit()
    {
        var s = await Scenario.StartAsync(postgres, "K");
        await DispatcherHarness.EnqueueAsync(s.Database, 200, key: _ => "K");

        Assert.Equal(1, await s.Database.ScalarAsync("SELECT count(*) FROM waybill.outbox WHERE key = 'K' AND sequence = 2 AND is_head IS NULL"));
        Assert.Equal(199, await s.Database.ScalarAsync("SELECT count(*) FROM waybill.outbox WHERE key = 'K' AND sequence > 2 AND is_head = false"));

        await s.PublishAsync(s.Heads["K"]);
        Assert.Equal(1, await s.Database.ScalarAsync("SELECT count(*) FROM waybill.outbox WHERE key = 'K' AND is_head AND status = 'pending'"));
        Assert.Equal(new long[] { 2 }, await s.ClaimSequencesAsync("K"));
    }

    // A limit (OPERATIONS.md): SET CONSTRAINTS ALL IMMEDIATE in the application runs the check at the insert, so the
    // predecessor stays locked FOR SHARE for the rest of the transaction, and the dispatcher's marking waits that long.
    // Order holds.
    [Fact]
    public async Task Limite_SetConstraintsImmediate_MarcacaoEsperaOProdutor()
    {
        var s = await Scenario.StartAsync(postgres, "K");
        await using var producer = new NpgsqlConnection(KeyLockHarness.ConnectionString(s.Database, App));
        await producer.OpenAsync(TestContext.Current.CancellationToken);
        await using var transaction = await producer.BeginTransactionAsync(TestContext.Current.CancellationToken);
        await KeyLockHarness.ExecuteAsync(producer, "SET CONSTRAINTS ALL IMMEDIATE");
        await KeyLockHarness.InsertAsync(producer, "K");

        var marking = s.PublishAsync(s.Heads["K"]);
        await KeyLockHarness.WaitForAsync(s.Database, atBarrier: 0, onLocks: 1);
        await transaction.CommitAsync(TestContext.Current.CancellationToken);
        await marking;

        Assert.Equal("2:pending:true", await s.UnfinishedAsync("K"));
    }

    /// <summary>One ordering dispatcher holding the only partition, and per key one committed head it has claimed.</summary>
    private sealed record Scenario(TestDatabase Database, NpgsqlDataSource DataSource, OutboxStore Store, string Owner, Dictionary<string, Guid> Heads)
    {
        public static async Task<Scenario> StartAsync(PostgresFixture postgres, params string[] keys)
        {
            var ct = TestContext.Current.CancellationToken;
            var database = await TestDatabase.CreateAsync(postgres);
            var dataSource = NpgsqlDataSource.Create(database.ConnectionString);
            var dispatcher = OrderingHarness.Create(dataSource, new FakeTransport(), OrderingHarness.Options(database, partitions: 1));
            Assert.Null(await dispatcher.CheckOrderingAsync(atStartup: true, ct));
            Assert.Equal(0, (await dispatcher.RunOnceAsync(ct)).Claimed); // takes the partition
            await DispatcherHarness.EnqueueAsync(database, keys.Length, key: i => keys[i]);
            var store = new OutboxStore(dataSource);
            var claimed = await store.ClaimAsync(dispatcher.Owner, 100, TimeSpan.FromSeconds(30), ct, partitions: 1);
            Assert.Equal(keys.Length, claimed.Count);
            return new Scenario(database, dataSource, store, dispatcher.Owner, claimed.ToDictionary(c => c.Message.Key!, c => c.Message.MessageId));
        }

        public static string PublishSql(Guid id) =>
            $"UPDATE waybill.outbox SET status = 'published', published_at = clock_timestamp(), lease_until = NULL WHERE id = '{id}'";

        public Task PublishAsync(Guid id) => Task.Run(() => Database.ExecuteAsync(PublishSql(id)));

        /// <summary>The key's rows from the first unfinished one on, as "sequence:status:is_head" (published rows only when asked through a connection that sees them so).</summary>
        public async Task<string> UnfinishedAsync(string key, NpgsqlConnection? connection = null)
        {
            var sql = $"""
                SELECT string_agg(sequence || ':' || status || ':' || coalesce(is_head::text, 'null'), ',' ORDER BY sequence)
                FROM waybill.outbox WHERE key = '{key}' AND (status IN ('pending', 'claimed', 'dlq') OR {(connection is null ? "false" : "true")})
                """;
            if (connection is not null)
            {
                await using var command = new NpgsqlCommand(sql, connection);
                return (string)(await command.ExecuteScalarAsync())!;
            }
            await using var own = DataSource.CreateCommand(sql);
            return (string)(await own.ExecuteScalarAsync())!;
        }

        public async Task<long[]> ClaimSequencesAsync(string key)
        {
            var claimed = await Store.ClaimAsync(Owner, 100, TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken, partitions: 1);
            return [.. claimed.Where(c => c.Message.Key == key).Select(c => c.Message.Sequence ?? -1)];
        }
    }
}
