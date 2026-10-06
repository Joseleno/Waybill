using Npgsql;

namespace Waybill.Tests.Integration;

[Collection(PostgresCollection.Name)]
public sealed class MatrizDeSuporteTests(PostgresFixture postgres)
{
    // Supported floor is PostgreSQL 15 (docs/escopo-e-fronteiras.md, Contrato técnico).
    [Fact]
    public async Task Postgres_VersaoDentroDaMatrizDeSuporte()
    {
        await using var connection = new NpgsqlConnection(postgres.ConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);

        await using var command = new NpgsqlCommand("SHOW server_version_num", connection);
        var version = int.Parse((string)(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken))!);

        Assert.True(version >= 150000, $"PostgreSQL {version} is below the supported floor (15) — image {PostgresFixture.Image}");
    }
}
