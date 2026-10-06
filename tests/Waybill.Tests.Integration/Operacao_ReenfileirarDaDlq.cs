using System.Text.RegularExpressions;
using Npgsql;
using Waybill.Tests.Integration.G2;

namespace Waybill.Tests.Integration;

// Until the operations API (v1.0), OPERATIONS.md documents the SQL that sends dead-lettered messages back to the
// dispatcher. This test runs that exact SQL, so the document cannot drift from a statement that works.
[Collection(PostgresCollection.Name)]
public sealed partial class Operacao_ReenfileirarDaDlq(PostgresFixture postgres)
{
    [Fact]
    public async Task Operacao_SqlDoOperationsReenfileiraDaDlq_EODispatcherPublica()
    {
        var ct = TestContext.Current.CancellationToken;
        var database = await TestDatabase.CreateAsync(postgres);
        await DispatcherHarness.EnqueueAsync(database, 1);
        await using var dataSource = NpgsqlDataSource.Create(database.ConnectionString);
        var routed = false; // no binding yet: every publish comes back unroutable
        var transport = new FakeTransport((_, batch, _) => Volatile.Read(ref routed)
            ? FakeTransport.ConfirmAll(batch)
            : Task.FromResult<IReadOnlyList<PublishResult>>(batch.Select(_ => new PublishResult(PublishStatus.Returned, "312 NO_ROUTE")).ToList()));
        var dispatcher = DispatcherHarness.Create(dataSource, transport, DispatcherHarness.Options(database, o => o.MaxReturns = 2));
        await dispatcher.RunOnceAsync(ct);
        await dispatcher.RunOnceAsync(ct);
        Assert.Equal(1, await database.CountAsync("dlq"));

        Volatile.Write(ref routed, true); // the binding now exists
        var sql = RequeueSql().Match(File.ReadAllText(Path.Combine(FindRoot(), "docs", "OPERATIONS.md")));
        Assert.True(sql.Success, "OPERATIONS.md has no <!-- dlq-requeue --> SQL block");
        await using (var command = dataSource.CreateCommand(sql.Groups[1].Value))
            Assert.Equal(1, await command.ExecuteNonQueryAsync(ct));
        await dispatcher.RunOnceAsync(ct);

        Assert.Equal(1, await database.CountAsync("published"));
        Assert.Equal(0, await database.CountAsync("dlq"));
    }

    private static string FindRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Waybill.slnx")))
                return dir.FullName;
        }
        throw new InvalidOperationException("Waybill.slnx not found above " + AppContext.BaseDirectory);
    }

    [GeneratedRegex(@"<!-- dlq-requeue -->\s*```sql\r?\n(.*?)```", RegexOptions.Singleline)]
    private static partial Regex RequeueSql();
}
