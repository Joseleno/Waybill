using System.Diagnostics;
using Npgsql;

namespace Waybill.Tests.Integration;

[Collection(PostgresCollection.Name)]
public sealed class PostgresFixtureTests(PostgresFixture postgres)
{
    // Every test gets its own database, so its own Npgsql pool. Idle connections kept for the default
    // 300 s piled up across tests until the server ran out of slots (53300 in CI, PR #19).
    [Fact]
    public async Task BancoDoTeste_ConexaoOciosaFechaLogo_SemAcumularEntreTestes()
    {
        var connectionString = await postgres.CreateDatabaseAsync();
        await using (var connection = new NpgsqlConnection(connectionString))
        {
            await connection.OpenAsync(TestContext.Current.CancellationToken);
        }

        var name = new NpgsqlConnectionStringBuilder(connectionString).Database!;
        await using var admin = new NpgsqlConnection(postgres.ConnectionString);
        await admin.OpenAsync(TestContext.Current.CancellationToken);
        await using var count = new NpgsqlCommand("SELECT count(*) FROM pg_stat_activity WHERE datname = @name", admin);
        count.Parameters.AddWithValue("name", name);

        var clock = Stopwatch.StartNew();
        long open;
        while ((open = (long)(await count.ExecuteScalarAsync(TestContext.Current.CancellationToken))!) > 0
               && clock.Elapsed < TimeSpan.FromSeconds(15))
            await Task.Delay(200, TestContext.Current.CancellationToken);

        Assert.Equal(0, open);
    }
}
