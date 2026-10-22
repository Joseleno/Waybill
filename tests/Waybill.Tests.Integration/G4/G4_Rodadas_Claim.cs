using System.Text.Json;
using Npgsql;
using Waybill.EntityFrameworkCore.Dispatching;
using Waybill.Tests.Integration.G2;

namespace Waybill.Tests.Integration.G4;

// The series claim (M ≥ 2, PLAN.md 8c-2): of each key, its head and up to M − 1 unfinished rows after it, in sequence
// order, cut by round (every second row before any third) to the batch, and only as long as every row before one in
// its key is in the batch too.
[Collection(PostgresCollection.Name)]
public sealed class G4_Rodadas_Claim(PostgresFixture postgres)
{
    private const string Owner = "instance";

    [Fact]
    public async Task G4_Rodadas_SerieDaCabeca_AteMNaOrdemDaSequence()
    {
        var ct = TestContext.Current.CancellationToken;
        var database = await TestDatabase.CreateAsync(postgres);
        var store = await OwnAllPartitionsAsync(database);
        await DispatcherHarness.EnqueueAsync(database, 5, key: _ => "A");
        await DispatcherHarness.EnqueueAsync(database, 1, key: _ => null);

        var claimed = await store.ClaimAsync(Owner, 100, TimeSpan.FromSeconds(30), ct, partitions: 4, maxPerKey: 3);

        Assert.Equal([(null, 0, 0), ("A", 1L, 0), ("A", 2L, 1), ("A", 3L, 2)],
            claimed.Select(c => (c.Message.Key, c.Sequence ?? 0, c.Rank)).OrderBy(c => c.Item2));
        Assert.All(claimed.Where(c => c.Message.Key == "A"), c => Assert.Equal(c.Sequence < 5, c.MoreOfKey));
    }

    // The claim itself says each row's round: the place it was locked at in its key's series (0 for the head). Ranks
    // worked out from the rows that came back could not tell a successor whose head dropped out of the statement from a
    // head; with the claim's own rank, the rounds refuse such a key instead of sending the successor first.
    [Fact]
    public async Task G4_Rodadas_RankVemDoClaim()
    {
        var ct = TestContext.Current.CancellationToken;
        var database = await TestDatabase.CreateAsync(postgres);
        await OwnAllPartitionsAsync(database);
        await DispatcherHarness.EnqueueAsync(database, 3, key: _ => "A");
        await DispatcherHarness.EnqueueAsync(database, 1, key: _ => null);

        await using var connection = new NpgsqlConnection(database.ConnectionString);
        await connection.OpenAsync(ct);
        await using var command = new NpgsqlCommand(OutboxStore.ClaimSeriesSql, connection);
        command.Parameters.Add(new NpgsqlParameter { Value = Owner });
        command.Parameters.Add(new NpgsqlParameter { Value = TimeSpan.FromSeconds(30), NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Interval });
        command.Parameters.Add(new NpgsqlParameter { Value = 10 });
        command.Parameters.Add(new NpgsqlParameter { Value = 4 });
        command.Parameters.Add(new NpgsqlParameter { Value = 3 });
        var ranks = new List<(long Sequence, int Rank)>();
        await using (var reader = await command.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
                ranks.Add((reader.IsDBNull(reader.GetOrdinal("sequence")) ? 0 : reader.GetInt64(reader.GetOrdinal("sequence")), reader.GetInt32(reader.GetOrdinal("rank"))));
        }

        Assert.Equal([(0L, 0), (1L, 0), (2L, 1), (3L, 2)], ranks.OrderBy(r => r.Sequence));
    }

    public static TheoryData<string, int> Shapes => new()
    {
        { "100 keys of 1000, empty statistics", 1 },
        { "100 keys of 1000, empty statistics", 100 },
        { "200 thousand without a key", 1 },
        { "200 thousand without a key", 100 },
    };

    // Measured in the 8c-2 review with the real schema, generic plan, PG 15 and 18: the prefix as a join of two CTEs
    // became a nested loop that compared every row with every other (809 thousand at B = 1000, 2.7 s at B = 5000); the
    // UPDATE as a join with the CTE, and the lock as IN, became a hash over a sequential scan of the whole outbox.
    [Theory]
    [MemberData(nameof(Shapes))]
    public async Task G4_Rodadas_PlanoGenerico_SemLacoQuadraticoNemSeqScan(string shape, int maxPerKey)
    {
        var database = await TestDatabase.CreateAsync(postgres);
        if (shape.StartsWith("100 keys"))
        {
            await SeedKeysAsync(database, keys: 100, perKey: 1000);
        }
        else
        {
            await SeedKeysAsync(database, keys: 1, perKey: 10_000);
            await database.ExecuteAsync("""
                INSERT INTO waybill.outbox (id, type, key_hash, payload, content_type)
                SELECT ('00000000-0000-7000-9000-' || lpad(to_hex(g), 12, '0'))::uuid, 'billing.invoice-paid.v1', 0, '\x7b7d', 'application/json'
                FROM generate_series(1, 200000) g;
                """);
            await database.ExecuteAsync("VACUUM ANALYZE waybill.outbox");
        }
        await OwnAllPartitionsAsync(database); // after the seeds: with ordering on, the trigger would number them again

        var batch = shape.StartsWith("100 keys") ? 1000 : 100;
        var plan = await GenericPlanAsync(database, batch, maxPerKey);

        Assert.Empty(Nodes(plan).Where(n => n.GetProperty("Node Type").GetString() == "Seq Scan"
            && n.TryGetProperty("Relation Name", out var relation) && relation.GetString() == "outbox").Select(n => n.ToString()));
        Assert.Empty(Nodes(plan).Where(n => n.TryGetProperty("Sort Space Type", out var space) && space.GetString() == "Disk").Select(n => n.ToString()));
        var discarded = Nodes(plan).Sum(n => n.TryGetProperty("Rows Removed by Join Filter", out var removed)
            ? removed.GetDouble() * n.GetProperty("Actual Loops").GetDouble() : 0);
        Assert.True(discarded <= 10 * batch, $"{discarded} rows discarded by join filters for a batch of {batch}");
    }

    /// <summary>Partitions 0..3 of P = 4 held by <see cref="Owner"/> for an hour, and the settings row (ordering on).</summary>
    internal static async Task<OutboxStore> OwnAllPartitionsAsync(TestDatabase database)
    {
        await KeyLockHarness.EnableOrderingAsync(database);
        await database.ExecuteAsync($"""
            INSERT INTO waybill.outbox_partitions (partition, owner, epoch, lease_until)
            SELECT p, '{Owner}', 1, clock_timestamp() + interval '1 hour' FROM generate_series(0, 3) p
            """);
        return new OutboxStore(NpgsqlDataSource.Create(database.ConnectionString));
    }

    /// <summary>
    /// <paramref name="keys"/> keys of <paramref name="perKey"/> pending rows each, numbered and marked as the triggers would.
    /// Before ordering is on (<see cref="OwnAllPartitionsAsync"/>): with it, the trigger numbers rows itself.
    /// </summary>
    internal static async Task SeedKeysAsync(TestDatabase database, int keys, int perKey)
    {
        await database.ExecuteAsync($"""
            INSERT INTO waybill.outbox (id, type, key, key_hash, sequence, payload, content_type)
            SELECT ('00000000-0000-7000-8000-' || lpad(to_hex(k * {perKey} + s), 12, '0'))::uuid, 'billing.invoice-paid.v1',
                   'key-' || k, 0, s, '\x7b7d', 'application/json'
            FROM generate_series(0, {keys - 1}) k, generate_series(1, {perKey}) s;
            INSERT INTO waybill.outbox_keys SELECT 'key-' || k, {perKey} FROM generate_series(0, {keys - 1}) k;
            """);
        await OrderingHarness.MarkHeadsAsync(database);
    }

    private static async Task<JsonElement> GenericPlanAsync(TestDatabase database, int batch, int maxPerKey)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var connection = new NpgsqlConnection(database.ConnectionString);
        await connection.OpenAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        await using (var prepare = new NpgsqlCommand(
            maxPerKey == 1
                ? $"SET LOCAL plan_cache_mode = force_generic_plan; SET LOCAL jit = off; PREPARE claim(text, interval, integer, integer) AS {OutboxStore.ClaimOrderedSql}"
                : $"SET LOCAL plan_cache_mode = force_generic_plan; SET LOCAL jit = off; PREPARE claim(text, interval, integer, integer, integer) AS {OutboxStore.ClaimSeriesSql}",
            connection, transaction))
            await prepare.ExecuteNonQueryAsync(ct);
        await using var explain = new NpgsqlCommand(
            $"EXPLAIN (ANALYZE, BUFFERS, FORMAT JSON) EXECUTE claim('{Owner}', '30 seconds', {batch}, 4{(maxPerKey == 1 ? "" : ", " + maxPerKey)})", connection, transaction);
        var json = (string)(await explain.ExecuteScalarAsync(ct))!;
        await transaction.RollbackAsync(ct);
        TestContext.Current.TestOutputHelper?.WriteLine(json);
        return JsonDocument.Parse(json).RootElement[0].GetProperty("Plan").Clone();
    }

    private static IEnumerable<JsonElement> Nodes(JsonElement node)
    {
        yield return node;
        if (node.TryGetProperty("Plans", out var children))
            foreach (var child in children.EnumerateArray())
                foreach (var descendant in Nodes(child))
                    yield return descendant;
    }
}
