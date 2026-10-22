using Npgsql;
using Waybill.Tests.Integration.G2;
using Waybill.Tests.Integration.G4;

namespace Waybill.Tests.Integration;

// OPERATIONS.md, "Unmarked heads": the query that finds a key's first unfinished message left unmarked by something
// outside Waybill, and the SQL that marks it, run exactly as written. Marking is always safe: the claim still decides
// the head.
[Collection(PostgresCollection.Name)]
public sealed class Operacao_MarcarCabecas(PostgresFixture postgres)
{
    [Fact]
    public async Task Operacao_SqlDoOperationsAchaEMarcaACabecaSemMarca_EAChaveSegue()
    {
        var ct = TestContext.Current.CancellationToken;
        var database = await TestDatabase.CreateAsync(postgres);
        await using var dataSource = NpgsqlDataSource.Create(database.ConnectionString);
        var transport = new FakeTransport();
        var dispatcher = OrderingHarness.Create(dataSource, transport, OrderingHarness.Options(database, partitions: 1));
        Assert.Null(await dispatcher.CheckOrderingAsync(atStartup: true, ct));
        await dispatcher.RunOnceAsync(ct);
        await DispatcherHarness.EnqueueAsync(database, 2, key: _ => "order-42");
        await database.ExecuteAsync("""
            ALTER TABLE waybill.outbox DISABLE TRIGGER outbox_head_promote;
            UPDATE waybill.outbox SET status = 'published', published_at = clock_timestamp() WHERE key = 'order-42' AND sequence = 1;
            ALTER TABLE waybill.outbox ENABLE TRIGGER outbox_head_promote;
            """);

        Assert.Equal([("order-42", 2L)], await KeySequencesAsync(dataSource, OperationsDoc.Sql("unmarked-heads")));
        Assert.Equal([("order-42", 2L)], await KeySequencesAsync(dataSource, OperationsDoc.Sql("mark-heads")));
        Assert.Empty(await KeySequencesAsync(dataSource, OperationsDoc.Sql("unmarked-heads")));

        await dispatcher.RunOnceAsync(ct);
        Assert.Equal([2L], transport.Received.Select(m => m.Sequence ?? -1));
    }

    private static async Task<List<(string, long)>> KeySequencesAsync(NpgsqlDataSource dataSource, string sql)
    {
        await using var command = dataSource.CreateCommand(sql);
        await using var reader = await command.ExecuteReaderAsync();
        var rows = new List<(string, long)>();
        while (await reader.ReadAsync())
            rows.Add((reader.GetString(0), reader.GetInt64(1)));
        return rows;
    }
}
