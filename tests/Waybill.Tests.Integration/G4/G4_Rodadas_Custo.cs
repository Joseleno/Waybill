using System.Text.Json;
using Npgsql;
using Waybill.EntityFrameworkCore.Dispatching;

namespace Waybill.Tests.Integration.G4;

// The series claim's cost (M ≥ 2, PLAN.md 8c-2), asserted in buffers: time on a shared CI runner is noise. Each figure
// comes from one run on a freshly vacuumed table; repeating a claim and rolling it back leaves dead index entries that
// grow the next run's buffers. Measured in the 8c-2 review with the real schema, PG 15 and 18: about 27 buffers per row
// claimed, in every shape below.
[Collection(PostgresCollection.Name)]
public sealed class G4_Rodadas_Custo(PostgresFixture postgres)
{
    private const int BuffersPerRow = 40;

    // 100 keys of 1000: the batch fills with series, and the cost grows with the batch, not with its square.
    [Theory]
    [InlineData(1000)]
    [InlineData(5000)]
    public async Task G4_Rodadas_LoteGrande_Linear(int batch)
    {
        var database = await TestDatabase.CreateAsync(postgres);
        await G4_Rodadas_Claim.SeedKeysAsync(database, keys: 100, perKey: 1000);
        await database.ExecuteAsync("VACUUM ANALYZE waybill.outbox");
        await G4_Rodadas_Claim.OwnAllPartitionsAsync(database);

        var (buffers, rows, _) = await MeasureAsync(database, batch, maxPerKey: 100);

        TestContext.Current.TestOutputHelper?.WriteLine($"B = {batch}: {rows} rows, {buffers} buffers");
        Assert.Equal(batch, rows);
        Assert.True(buffers <= BuffersPerRow * rows, $"{buffers} buffers for {rows} rows");
    }

    // 500 keys of 200 with M = 100 and a batch of 1000: 500 heads leave 500 places. Each series reads its share of them,
    // not M − 1 rows: reading 99 rows of each key to use one would be 49.5 thousand rows and a sort spilled to disk.
    [Fact]
    public async Task G4_Rodadas_SerieNaoLeAlemDoOrcamento()
    {
        var database = await TestDatabase.CreateAsync(postgres);
        await G4_Rodadas_Claim.SeedKeysAsync(database, keys: 500, perKey: 200);
        await database.ExecuteAsync("VACUUM ANALYZE waybill.outbox");
        await G4_Rodadas_Claim.OwnAllPartitionsAsync(database);

        var (buffers, rows, plan) = await MeasureAsync(database, 1000, maxPerKey: 100);

        var successorsRead = Nodes(plan)
            .Where(n => n.TryGetProperty("Alias", out var alias) && alias.GetString() == "x")
            .Sum(n => n.GetProperty("Actual Rows").GetDouble() * n.GetProperty("Actual Loops").GetDouble());
        TestContext.Current.TestOutputHelper?.WriteLine($"{rows} rows, {buffers} buffers, {successorsRead} successors read");
        Assert.Equal(1000, rows);
        Assert.True(successorsRead <= 2 * 500, $"{successorsRead} successors read for 500 places");
        Assert.True(buffers <= BuffersPerRow * rows, $"{buffers} buffers for {rows} rows");
    }

    // One hot key alone with M = 100: the head and 99 successors per claim, at the same cost per row whatever the queue
    // behind (the B-tree grows a level between 10 thousand and 100 thousand: a little more, not in proportion).
    [Fact]
    public async Task G4_ChaveQuenteSozinha_DrenoLinear_ComMCem()
    {
        var perRow = new List<double>();
        foreach (var queue in new[] { 10_000, 100_000 })
        {
            var database = await TestDatabase.CreateAsync(postgres);
            await G4_Rodadas_Claim.SeedKeysAsync(database, keys: 1, perKey: queue);
            await database.ExecuteAsync("VACUUM ANALYZE waybill.outbox");
            await G4_Rodadas_Claim.OwnAllPartitionsAsync(database);

            var (buffers, rows, _) = await MeasureAsync(database, 100, maxPerKey: 100);

            TestContext.Current.TestOutputHelper?.WriteLine($"{queue} queued: {rows} rows, {buffers} buffers");
            Assert.Equal(100, rows);
            Assert.True(buffers <= BuffersPerRow * rows, $"{buffers} buffers for {rows} rows");
            perRow.Add(buffers / (double)rows);
        }
        Assert.True(perRow[1] <= 1.3 * perRow[0], $"{perRow[0]:F1} buffers per row at 10 thousand, {perRow[1]:F1} at 100 thousand");
    }

    private static async Task<(long Buffers, long Rows, JsonElement Plan)> MeasureAsync(TestDatabase database, int batch, int maxPerKey)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var connection = new NpgsqlConnection(database.ConnectionString);
        await connection.OpenAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        await using (var noJit = new NpgsqlCommand("SET LOCAL jit = off", connection, transaction))
            await noJit.ExecuteNonQueryAsync(ct);
        await using var command = new NpgsqlCommand("EXPLAIN (ANALYZE, BUFFERS, FORMAT JSON) " + OutboxStore.ClaimSeriesSql, connection, transaction);
        command.Parameters.Add(new NpgsqlParameter { Value = "instance" });
        command.Parameters.Add(new NpgsqlParameter { Value = TimeSpan.FromSeconds(30), NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Interval });
        command.Parameters.Add(new NpgsqlParameter { Value = batch });
        command.Parameters.Add(new NpgsqlParameter { Value = 4 });
        command.Parameters.Add(new NpgsqlParameter { Value = maxPerKey });
        var plan = JsonDocument.Parse((string)(await command.ExecuteScalarAsync(ct))!).RootElement[0].GetProperty("Plan").Clone();
        await transaction.RollbackAsync(ct);
        return (plan.GetProperty("Shared Hit Blocks").GetInt64() + plan.GetProperty("Shared Read Blocks").GetInt64(),
            (long)plan.GetProperty("Actual Rows").GetDouble(), plan);
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
