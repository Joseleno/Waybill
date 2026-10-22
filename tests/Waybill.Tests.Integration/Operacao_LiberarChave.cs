using Npgsql;
using Waybill.Tests.Integration.G4;

namespace Waybill.Tests.Integration;

// Until the operations API (v1.0), OPERATIONS.md documents the SQL that releases a key stopped by the DLQ. This test
// runs that exact SQL: the message stays as released, with who and when, the key moves on in order, and the gauge drops.
[Collection(PostgresCollection.Name)]
public sealed class Operacao_LiberarChave(PostgresFixture postgres)
{
    [Fact]
    public async Task Operacao_SqlDoOperationsLiberaAChave_EElaVoltaAFluirEmOrdem()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var scenario = await DlqScenario.StartAsync(postgres);
        await scenario.Dispatcher.RunOnceAsync(ct);
        Assert.Equal("1:dlq,2:pending,3:pending", await scenario.StatusesAsync("order-42"));

        var released = new List<long>();
        await using (var command = scenario.DataSource.CreateCommand(OperationsDoc.Sql("key-release")))
        await using (var reader = await command.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
                released.Add(reader.GetInt64(1));
        }

        Assert.Equal([1L], released); // the gap the consumers will see
        Assert.Equal(1, await scenario.Database.ScalarAsync(
            "SELECT count(*) FROM waybill.outbox WHERE key = 'order-42' AND sequence = 1 AND status = 'released' AND released_at IS NOT NULL AND released_by = session_user AND dlq_reason = 'rejected by the test'"));
        scenario.Fix();
        var before = scenario.Transport.Received.Count;
        for (var i = 0; i < 3; i++)
            await scenario.Dispatcher.RunOnceAsync(ct);

        var sent = scenario.Transport.Received.Skip(before).Where(m => m.Key == "order-42").Select(m => m.MessageId).ToList();
        Assert.Equal([2L, 3L], await SequencesAsync(scenario.DataSource, sent));
        Assert.Equal("1:released,2:published,3:published", await scenario.StatusesAsync("order-42"));
        Assert.Equal(0, await scenario.BlockedKeysAsync(ct));
    }

    // Finishing a key's head marks the next message, and that needs to see messages committed during the transaction:
    // at REPEATABLE READ or SERIALIZABLE the snapshot is older, a producer's message committed meanwhile would be left
    // unmarked and the key would stop. The database refuses it, and the release runs again in READ COMMITTED.
    [Theory]
    [InlineData("REPEATABLE READ")]
    [InlineData("SERIALIZABLE")]
    public async Task Operacao_LiberarForaDeReadCommitted_Recusa(string isolation)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var scenario = await DlqScenario.StartAsync(postgres);
        await scenario.Dispatcher.RunOnceAsync(ct);

        await using var connection = await scenario.DataSource.OpenConnectionAsync(ct);
        await using (var begin = new NpgsqlCommand($"BEGIN ISOLATION LEVEL {isolation}", connection))
            await begin.ExecuteNonQueryAsync(ct);
        await using var release = new NpgsqlCommand(OperationsDoc.Sql("key-release"), connection);
        var error = await Assert.ThrowsAsync<PostgresException>(() => release.ExecuteNonQueryAsync(ct));

        Assert.Contains("READ COMMITTED", error.MessageText);
        Assert.Equal("1:dlq,2:pending,3:pending", await scenario.StatusesAsync("order-42"));
    }

    private static async Task<List<long>> SequencesAsync(NpgsqlDataSource dataSource, List<Guid> ids)
    {
        var sequences = new List<long>();
        foreach (var id in ids)
        {
            await using var command = dataSource.CreateCommand($"SELECT sequence FROM waybill.outbox WHERE id = '{id}'");
            sequences.Add((long)(await command.ExecuteScalarAsync())!);
        }
        return sequences;
    }
}
