using System.Text.RegularExpressions;
using Npgsql;
using Waybill.EntityFrameworkCore.Dispatching;
using Waybill.Tests.Integration.G2;
using Waybill.Tests.Integration.G4;

namespace Waybill.Tests.Integration;

// OPERATIONS.md documents the SQL that clears the ordering state, to turn ordering off or change P with every
// dispatcher stopped. This test runs that exact SQL: afterwards a dispatcher with another P starts, and one without
// ordering does too.
[Collection(PostgresCollection.Name)]
public sealed partial class Operacao_ReiniciarOrdenacao(PostgresFixture postgres)
{
    [Fact]
    public async Task Operacao_SqlDoOperationsReiniciaOrdenacao_NovoPOuSemOrdenacaoSobem()
    {
        var ct = TestContext.Current.CancellationToken;
        var database = await TestDatabase.CreateAsync(postgres);
        await using var dataSource = NpgsqlDataSource.Create(database.ConnectionString);
        var before = OrderingHarness.Create(dataSource, new FakeTransport(), OrderingHarness.Options(database, partitions: 4));
        Assert.Null(await before.CheckOrderingAsync(atStartup: true, ct));
        await before.RunOnceAsync(ct); // holds the four partitions, and is then stopped without handing them back

        var sql = ResetSql().Match(File.ReadAllText(Path.Combine(FindRoot(), "docs", "OPERATIONS.md")));
        Assert.True(sql.Success, "OPERATIONS.md has no <!-- ordering-reset --> SQL block");
        await using (var command = dataSource.CreateCommand(sql.Groups[1].Value))
            await command.ExecuteNonQueryAsync(ct);

        var unordered = DispatcherHarness.Create(dataSource, new FakeTransport(), DispatcherHarness.Options(database));
        Assert.Null(await unordered.CheckOrderingAsync(atStartup: true, ct));

        var after = OrderingHarness.Create(dataSource, new FakeTransport(), OrderingHarness.Options(database, partitions: 8));
        Assert.Null(await after.CheckOrderingAsync(atStartup: true, ct));
        await after.RunOnceAsync(ct);
        Assert.Equal(8, after.HeldPartitions.Count);
        Assert.Equal(new OrderingSettings(8, TimeSpan.FromSeconds(60)), await new PartitionStore(dataSource).ReadSettingsAsync(ct));
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

    [GeneratedRegex(@"<!-- ordering-reset -->\s*```sql\r?\n(.*?)```", RegexOptions.Singleline)]
    private static partial Regex ResetSql();
}
