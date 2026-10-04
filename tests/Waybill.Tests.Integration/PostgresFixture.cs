using Testcontainers.PostgreSql;

namespace Waybill.Tests.Integration;

/// <summary>
/// One PostgreSQL container per test collection. The image comes from WAYBILL_POSTGRES_IMAGE so CI can run
/// the suite against every supported version (15 and 18); locally it defaults to the newest.
/// </summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    public static string Image => Environment.GetEnvironmentVariable("WAYBILL_POSTGRES_IMAGE") ?? "postgres:18-alpine";

    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder(Image).Build();

    public string ConnectionString => _container.GetConnectionString();

    public ValueTask InitializeAsync() => new(_container.StartAsync());

    public ValueTask DisposeAsync() => _container.DisposeAsync();
}

[CollectionDefinition(Name)]
public sealed class PostgresCollection : ICollectionFixture<PostgresFixture>
{
    public const string Name = "postgres";
}
