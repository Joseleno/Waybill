using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Waybill.EntityFrameworkCore;
using Waybill.EntityFrameworkCore.Schema;

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
        // Ordering by key (ADR 0007): global settings, partition ownership, live instances.
        Assert.Equal(3, await database.ScalarAsync("SELECT count(*) FROM information_schema.tables WHERE table_schema = 'waybill' AND table_name IN ('settings', 'outbox_partitions', 'outbox_instances')"));
        Assert.Equal(1, await database.ScalarAsync("SELECT count(*) FROM pg_indexes WHERE schemaname = 'waybill' AND indexname = 'ix_outbox_claimable' AND indexdef LIKE '%WHERE%'"));
        // Retention deletes inbox rows by age; the message_id may not be a UUIDv7, so it needs its own index (ADR 0004).
        Assert.Equal(1, await database.ScalarAsync("SELECT count(*) FROM pg_indexes WHERE schemaname = 'waybill' AND indexname = 'ix_inbox_processed_at' AND indexdef LIKE '%(processed_at)%'"));
        Assert.Equal(MigrationCount, await database.ScalarAsync("SELECT count(*) FROM waybill.__waybill_migrations"));
        Assert.Equal(0, await database.ScalarAsync("SELECT count(*) FROM information_schema.tables WHERE table_schema = 'public'"));
        await AssertOrderingObjectsAsync(database.ScalarAsync);
    }

    // Ordering by key (ADR 0008): the per-key counter, the outbox's new columns, the head and blocked-key indexes (valid),
    // the numbering and terminal-status triggers, and the status constraint with 'released', validated.
    private static async Task AssertOrderingObjectsAsync(Func<string, Task<long>> scalar)
    {
        Assert.Equal(1, await scalar("SELECT count(*) FROM pg_class WHERE oid = 'waybill.outbox_keys'::regclass AND reloptions @> '{fillfactor=80}'"));
        Assert.Equal(3, await scalar("SELECT count(*) FROM information_schema.columns WHERE table_schema = 'waybill' AND table_name = 'outbox' AND column_name IN ('lock_keys', 'released_at', 'released_by')"));
        Assert.Equal(2, await scalar("""
            SELECT count(*) FROM pg_index i JOIN pg_class c ON c.oid = i.indexrelid
            WHERE c.relname IN ('ix_outbox_key_sequence', 'ix_outbox_blocked_keys') AND i.indisvalid AND i.indpred IS NOT NULL
            """));
        Assert.Equal(2, await scalar("SELECT count(*) FROM pg_trigger WHERE tgrelid = 'waybill.outbox'::regclass AND tgname IN ('outbox_sequence', 'outbox_terminal_guard') AND tgenabled = 'O'"));
        Assert.Equal(1, await scalar("SELECT count(*) FROM pg_constraint WHERE conname = 'ck_outbox_status' AND convalidated AND pg_get_constraintdef(oid) LIKE '%released%'"));
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
        Assert.Equal(MigrationCount, await database.ScalarAsync("SELECT count(*) FROM waybill.__waybill_migrations"));
        Assert.Equal(0, await database.ScalarAsync("SELECT count(*) FROM waybill.outbox"));
    }

    // A database left by 0.1.0-alpha, with messages still pending, takes the v0.2 migration: it only adds, rewrites no
    // pending row, and the backlog drains as before.
    [Fact]
    public async Task Schema_UpgradeDaV01ComBacklog_LinhasContinuamReivindicaveis()
    {
        var ct = TestContext.Current.CancellationToken;
        var database = await TestDatabase.CreateAsync(postgres, untilMigration: LastV01Migration);
        await G2.DispatcherHarness.EnqueueAsync(database, 20);

        await WaybillSchema.MigrateAsync(database.ConnectionString, ct);

        Assert.Equal(20, await database.ScalarAsync("SELECT count(*) FROM waybill.outbox WHERE status = 'pending' AND next_attempt_at IS NULL AND attempts = 0 AND fence = 0"));
        Assert.Equal(20, await database.ScalarAsync("SELECT count(*) FROM waybill.outbox WHERE sequence IS NULL AND lock_keys IS NULL AND released_at IS NULL"));
        Assert.Equal(0, await database.ScalarAsync("SELECT count(*) FROM waybill.outbox_keys"));
        await AssertOrderingObjectsAsync(database.ScalarAsync);
        await using var dataSource = Npgsql.NpgsqlDataSource.Create(database.ConnectionString);
        var transport = new G2.FakeTransport();
        var dispatcher = G2.DispatcherHarness.Create(dataSource, transport, G2.DispatcherHarness.Options(database));
        await dispatcher.RunOnceAsync(ct);
        Assert.Equal(20, await G2.DispatcherHarness.CountAsync(database, "published"));
    }

    private const string LastV01Migration = "20261004233339_InboxProcessedAtIndex";

    // The v0.2 migration builds its indexes CONCURRENTLY, outside the migration's transaction: a failure there (timeout,
    // cancel, lost connection) leaves the earlier steps committed, the history unwritten and the index invalid. Running
    // the migration again must finish it, rebuilding the invalid index instead of keeping it.
    [Fact]
    public async Task Schema_MigracaoInterrompidaNoIndice_ReexecutarCompleta()
    {
        var ct = TestContext.Current.CancellationToken;
        var database = await TestDatabase.CreateAsync(postgres);
        await database.ExecuteAsync("""
            DELETE FROM waybill.__waybill_migrations WHERE "MigrationId" LIKE '%_SchemaV0_2';
            UPDATE pg_index SET indisvalid = false WHERE indexrelid = 'waybill.ix_outbox_key_sequence'::regclass;
            ALTER TABLE waybill.outbox DROP CONSTRAINT ck_outbox_status;
            ALTER TABLE waybill.outbox ADD CONSTRAINT ck_outbox_status
                CHECK (status IN ('pending', 'claimed', 'published', 'dlq', 'released')) NOT VALID;
            """);

        await WaybillSchema.MigrateAsync(database.ConnectionString, ct);

        Assert.Equal(MigrationCount, await database.ScalarAsync("SELECT count(*) FROM waybill.__waybill_migrations"));
        await AssertOrderingObjectsAsync(database.ScalarAsync);
    }

    // The schema only moves forward once ordering has numbered a key or released a row: going back would drop the
    // counters, and a key starting again at 1 looks like a regression to its consumers.
    [Theory]
    [InlineData("INSERT INTO waybill.outbox_keys VALUES ('invoice-1', 3)")]
    [InlineData("""
        INSERT INTO waybill.outbox (id, type, key_hash, payload, content_type, status, released_at, released_by)
        VALUES (gen_random_uuid(), 'billing.invoice-paid.v1', 0, '\x00', 'application/json', 'released', clock_timestamp(), 'ops')
        """)]
    public async Task Schema_DownComOrdenacaoUsada_Recusa(string used)
    {
        var database = await TestDatabase.CreateAsync(postgres);
        await database.ExecuteAsync(used);

        await using var schema = new WaybillSchemaContext(WaybillSchemaContext.Options(database.ConnectionString));
        var error = await Assert.ThrowsAsync<Npgsql.PostgresException>(() => schema.GetService<IMigrator>().MigrateAsync(LastV01Migration, TestContext.Current.CancellationToken));

        Assert.Contains("only moves forward", error.MessageText);
        Assert.Equal(MigrationCount, await database.ScalarAsync("SELECT count(*) FROM waybill.__waybill_migrations"));
    }

    [Fact]
    public async Task Schema_DownSemOrdenacaoUsada_VoltaAV01EVolta()
    {
        var ct = TestContext.Current.CancellationToken;
        var database = await TestDatabase.CreateAsync(postgres);
        await using var schema = new WaybillSchemaContext(WaybillSchemaContext.Options(database.ConnectionString));

        await schema.GetService<IMigrator>().MigrateAsync(LastV01Migration, ct);
        Assert.Equal(0, await database.ScalarAsync("SELECT count(*) FROM information_schema.tables WHERE table_schema = 'waybill' AND table_name IN ('outbox_keys', 'settings', 'outbox_partitions', 'outbox_instances')"));
        Assert.Equal(0, await database.ScalarAsync("SELECT count(*) FROM pg_trigger WHERE tgrelid = 'waybill.outbox'::regclass AND NOT tgisinternal"));
        Assert.Equal(0, await database.ScalarAsync("SELECT count(*) FROM information_schema.columns WHERE table_schema = 'waybill' AND table_name = 'outbox' AND column_name IN ('next_attempt_at', 'lock_keys', 'released_at', 'released_by')"));

        await WaybillSchema.MigrateAsync(database.ConnectionString, ct);
        await AssertOrderingObjectsAsync(database.ScalarAsync);
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
    public async Task Configuracao_ContextoSemMapWaybillOutbox_FalhaNoEnqueue()
    {
        var connectionString = await postgres.CreateDatabaseAsync();
        var services = new ServiceCollection()
            .AddLogging()
            .AddWaybill(o =>
            {
                o.MaxPayloadBytes = 1024;
                o.AddMessage("billing.invoice-paid.v1", TestJson.Default.InvoicePaid);
            })
            .AddDbContext<UnmappedDbContext>(o => o.UseNpgsql(connectionString))
            .AddWaybillOutbox<UnmappedDbContext>()
            .BuildServiceProvider();
        await using var scope = services.CreateAsyncScope();
        var outbox = scope.ServiceProvider.GetRequiredService<IOutbox<UnmappedDbContext>>();

        var error = Assert.Throws<InvalidOperationException>(() => outbox.Enqueue(new InvoicePaid(Guid.NewGuid(), 1m)));
        Assert.Contains("MapWaybillOutbox()", error.Message);
    }

    // Unsupported by the contract, and dangerous: the outbox row could commit apart from the data.
    [Fact]
    public async Task Configuracao_AutoTransactionNeverSemTransacao_FalhaNoEnqueue()
    {
        var database = await TestDatabase.CreateAsync(postgres);
        await using var services = database.Services();
        await using var scope = services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var outbox = scope.ServiceProvider.GetRequiredService<IOutbox<AppDbContext>>();
        context.Database.AutoTransactionBehavior = AutoTransactionBehavior.Never;

        var error = Assert.Throws<InvalidOperationException>(() => outbox.Enqueue(new InvoicePaid(Guid.NewGuid(), 1m)));
        Assert.Contains("AutoTransactionBehavior.Never", error.Message);

        await using var transaction = await context.Database.BeginTransactionAsync(TestContext.Current.CancellationToken);
        outbox.Enqueue(new InvoicePaid(Guid.NewGuid(), 1m)); // fine inside an explicit transaction
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Configuracao_TransactionScope_FalhaNoEnqueue()
    {
        var database = await TestDatabase.CreateAsync(postgres);
        await using var services = database.Services();
        await using var scope = services.CreateAsyncScope();
        var outbox = scope.ServiceProvider.GetRequiredService<IOutbox<AppDbContext>>();

        using var ambient = new System.Transactions.TransactionScope(System.Transactions.TransactionScopeAsyncFlowOption.Enabled);
        var error = Assert.Throws<InvalidOperationException>(() => outbox.Enqueue(new InvoicePaid(Guid.NewGuid(), 1m)));
        Assert.Contains("TransactionScope", error.Message);
    }

    // Every migration shipped in the package is applied, once.
    private static readonly long MigrationCount = typeof(WaybillSchema).Assembly.GetTypes()
        .Count(t => t.IsSubclassOf(typeof(Migration)) && !t.IsAbstract);

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
