using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Waybill.EntityFrameworkCore;

namespace Waybill.Tests.Integration;

[Collection(PostgresCollection.Name)]
public sealed class SchemaTests(PostgresFixture postgres)
{
    [Fact]
    public async Task Schema_MigraBancoLimpo()
    {
        var database = new Database(await postgres.CreateDatabaseAsync());

        await WaybillSchema.MigrateAsync(database.ConnectionString, TestContext.Current.CancellationToken);

        Assert.Equal(1, await database.ScalarAsync("SELECT count(*) FROM information_schema.tables WHERE table_schema = 'waybill' AND table_name = 'outbox'"));
        Assert.Equal(1, await database.ScalarAsync("SELECT count(*) FROM information_schema.tables WHERE table_schema = 'waybill' AND table_name = 'inbox'"));
        Assert.Equal(1, await database.ScalarAsync("SELECT count(*) FROM pg_indexes WHERE schemaname = 'waybill' AND indexname = 'ix_outbox_claimable' AND indexdef LIKE '%WHERE%'"));
        Assert.Equal(1, await database.ScalarAsync("SELECT count(*) FROM waybill.__waybill_migrations"));
        Assert.Equal(0, await database.ScalarAsync("SELECT count(*) FROM information_schema.tables WHERE table_schema = 'public'"));
    }

    [Fact]
    public async Task Schema_MigraBancoComDados()
    {
        var connectionString = await postgres.CreateDatabaseAsync();
        var database = new Database(connectionString);
        await database.ExecuteAsync("""
            CREATE TABLE invoices (id uuid PRIMARY KEY, number text NOT NULL);
            INSERT INTO invoices VALUES (gen_random_uuid(), 'INV-1'), (gen_random_uuid(), 'INV-2');
            CREATE TABLE "__EFMigrationsHistory" ("MigrationId" text PRIMARY KEY, "ProductVersion" text NOT NULL);
            INSERT INTO "__EFMigrationsHistory" VALUES ('20260101000000_AppInitial', '10.0.0');
            """);

        await WaybillSchema.MigrateAsync(connectionString, TestContext.Current.CancellationToken);
        await WaybillSchema.MigrateAsync(connectionString, TestContext.Current.CancellationToken); // idempotent

        Assert.Equal(2, await database.ScalarAsync("SELECT count(*) FROM invoices"));
        Assert.Equal(1, await database.ScalarAsync("SELECT count(*) FROM \"__EFMigrationsHistory\""));
        Assert.Equal(1, await database.ScalarAsync("SELECT count(*) FROM waybill.__waybill_migrations"));
        Assert.Equal(0, await database.ScalarAsync("SELECT count(*) FROM waybill.outbox"));
    }

    [Fact]
    public async Task Schema_ModeloDoUsuarioNaoGeraMigrationDaOutbox()
    {
        var database = await TestDatabase.CreateAsync(postgres);
        await using var services = database.Services();
        await using var scope = services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var model = context.GetService<IDesignTimeModel>().Model;
        var operations = context.GetService<IMigrationsModelDiffer>().GetDifferences(null, model.GetRelationalModel());

        Assert.NotEmpty(operations);
        Assert.DoesNotContain(operations, o => o is EnsureSchemaOperation { Name: "waybill" });
        Assert.DoesNotContain(operations, o => o is CreateTableOperation { Schema: "waybill" });
        Assert.Contains(operations, o => o is CreateTableOperation { Name: "invoices" });
    }

    [Fact]
    public async Task Configuracao_ContextoSemAddWaybillOutbox_FalhaNoEnqueue()
    {
        var connectionString = await postgres.CreateDatabaseAsync();
        var services = new ServiceCollection()
            .AddLogging()
            .AddWaybill(o =>
            {
                o.MaxPayloadBytes = 1024;
                o.AddMessage("billing.invoice-paid.v1", TestJson.Default.InvoicePaid);
            })
            .AddDbContext<UnmappedDbContext>(o => o.UseNpgsql(connectionString).UseWaybill())
            .AddWaybillOutbox<UnmappedDbContext>()
            .BuildServiceProvider();
        await using var scope = services.CreateAsyncScope();
        var outbox = scope.ServiceProvider.GetRequiredService<IOutbox<UnmappedDbContext>>();

        var error = Assert.Throws<InvalidOperationException>(() => outbox.Enqueue(new InvoicePaid(Guid.NewGuid(), 1m)));
        Assert.Contains("AddWaybillOutbox()", error.Message);
    }

    [Fact]
    public async Task Configuracao_ContextoSemUseWaybill_FalhaNoEnqueue()
    {
        var connectionString = await postgres.CreateDatabaseAsync();
        var services = new ServiceCollection()
            .AddLogging()
            .AddWaybill(o =>
            {
                o.MaxPayloadBytes = 1024;
                o.AddMessage("billing.invoice-paid.v1", TestJson.Default.InvoicePaid);
            })
            .AddDbContext<AppDbContext>(o => o.UseNpgsql(connectionString))
            .AddWaybillOutbox<AppDbContext>()
            .BuildServiceProvider();
        await using var scope = services.CreateAsyncScope();
        var outbox = scope.ServiceProvider.GetRequiredService<IOutbox<AppDbContext>>();

        var error = Assert.Throws<InvalidOperationException>(() => outbox.Enqueue(new InvoicePaid(Guid.NewGuid(), 1m)));
        Assert.Contains("UseWaybill()", error.Message);
    }

    public sealed class UnmappedDbContext(DbContextOptions<UnmappedDbContext> options) : DbContext(options);

    private sealed record Database(string ConnectionString)
    {
        public async Task<long> ScalarAsync(string sql)
        {
            await using var connection = new Npgsql.NpgsqlConnection(ConnectionString);
            await connection.OpenAsync();
            await using var command = new Npgsql.NpgsqlCommand(sql, connection);
            return Convert.ToInt64(await command.ExecuteScalarAsync());
        }

        public async Task ExecuteAsync(string sql)
        {
            await using var connection = new Npgsql.NpgsqlConnection(ConnectionString);
            await connection.OpenAsync();
            await using var command = new Npgsql.NpgsqlCommand(sql, connection);
            await command.ExecuteNonQueryAsync();
        }
    }
}
