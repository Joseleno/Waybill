using Npgsql;
using Waybill.Tests.Integration.G4;

namespace Waybill.Tests.Integration;

// The diagnostic queries OPERATIONS.md gives for ordering by key, run as written: which keys the DLQ stops and what
// waits behind them, which keys wait after a basic.return, and everything not yet published of one key.
[Collection(PostgresCollection.Name)]
public sealed class Operacao_ConsultasDeDiagnostico(PostgresFixture postgres)
{
    [Fact]
    public async Task Operacao_ConsultasDoOperations_ApontamAChaveOMotivoEOQueEspera()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var scenario = await DlqScenario.StartAsync(postgres, returnedKey: "waiting-7");
        await scenario.Dispatcher.RunOnceAsync(ct);

        var blocked = await RowsAsync(scenario.DataSource, OperationsDoc.Sql("blocked-keys"));
        var stopped = Assert.Single(blocked);
        Assert.Equal(("order-42", 1L, "rejected by the test", 2L), ((string)stopped[0], (long)stopped[1], (string)stopped[3], (long)stopped[4]));

        var waiting = Assert.Single(await RowsAsync(scenario.DataSource, OperationsDoc.Sql("waiting-heads")));
        Assert.Equal(("waiting-7", 1L, 1), ((string)waiting[0], (long)waiting[1], (int)waiting[3]));

        var rows = await RowsAsync(scenario.DataSource, OperationsDoc.Sql("key-rows"));
        Assert.Equal([1L, 2L, 3L], rows.Select(r => (long)r[0]));
        Assert.Equal(["dlq", "pending", "pending"], rows.Select(r => (string)r[2]));
        Assert.All(rows, r => Assert.Equal(scenario.Dispatcher.Owner, r[8]));
    }

    private static async Task<List<object[]>> RowsAsync(NpgsqlDataSource dataSource, string sql)
    {
        await using var command = dataSource.CreateCommand(sql);
        await using var reader = await command.ExecuteReaderAsync();
        var rows = new List<object[]>();
        while (await reader.ReadAsync())
        {
            var row = new object[reader.FieldCount];
            reader.GetValues(row);
            rows.Add(row);
        }
        return rows;
    }
}
