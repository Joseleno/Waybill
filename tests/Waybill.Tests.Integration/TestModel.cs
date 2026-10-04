using System.Collections.Concurrent;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;
using Npgsql.EntityFrameworkCore.PostgreSQL.Infrastructure;
using Waybill.EntityFrameworkCore;

namespace Waybill.Tests.Integration;

public sealed record InvoicePaid(Guid InvoiceId, decimal Amount);

public sealed record AuditRecorded(string Action);

[JsonSerializable(typeof(InvoicePaid))]
[JsonSerializable(typeof(AuditRecorded))]
internal sealed partial class TestJson : JsonSerializerContext;

public sealed class Invoice
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public required string Number { get; set; }
    public decimal Amount { get; set; }
}

public sealed class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<Invoice> Invoices => Set<Invoice>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Invoice>(invoice =>
        {
            invoice.ToTable("invoices");
            invoice.HasIndex(i => i.Number).IsUnique();
        });
        modelBuilder.AddWaybillOutbox();
    }
}

public sealed class AuditEntry
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public required string Action { get; set; }
}

public sealed class AuditDbContext(DbContextOptions<AuditDbContext> options) : DbContext(options)
{
    public DbSet<AuditEntry> Entries => Set<AuditEntry>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<AuditEntry>().ToTable("audit_entries");
        modelBuilder.AddWaybillOutbox();
    }
}

/// <summary>A test database with the Waybill schema migrated and the application's tables created.</summary>
public sealed class TestDatabase
{
    private TestDatabase(string connectionString) => ConnectionString = connectionString;

    public string ConnectionString { get; }

    public static async Task<TestDatabase> CreateAsync(PostgresFixture postgres)
    {
        var database = new TestDatabase(await postgres.CreateDatabaseAsync());
        await WaybillSchema.MigrateAsync(database.ConnectionString);

        await using var services = database.Services();
        await using var scope = services.CreateAsyncScope();
        foreach (var context in new DbContext[]
                 {
                     scope.ServiceProvider.GetRequiredService<AppDbContext>(),
                     scope.ServiceProvider.GetRequiredService<AuditDbContext>(),
                 })
        {
            // CreateTables (not EnsureCreated): the database already has Waybill's tables, and EnsureCreated
            // does nothing when any table exists. The outbox mapping is excluded from this, like from migrations.
            await context.GetService<IRelationalDatabaseCreator>().CreateTablesAsync();
        }
        return database;
    }

    /// <summary>The application's container: Waybill configured, both contexts with an outbox, logs captured.</summary>
    public ServiceProvider Services(
        Action<WaybillOptions>? configure = null,
        LogSink? logs = null,
        Action<NpgsqlDbContextOptionsBuilder>? npgsql = null,
        Func<IServiceProvider, NpgsqlConnection>? sharedConnection = null,
        params Microsoft.EntityFrameworkCore.Diagnostics.IInterceptor[] interceptors)
    {
        var services = new ServiceCollection();
        services.AddLogging(logging => logging.AddProvider(logs ?? new LogSink()));
        services.AddWaybill(options =>
        {
            options.MaxPayloadBytes = 64 * 1024;
            options.AddMessage("billing.invoice-paid.v1", TestJson.Default.InvoicePaid);
            options.AddMessage("audit.recorded.v1", TestJson.Default.AuditRecorded);
            configure?.Invoke(options);
        });

        void Configure(IServiceProvider sp, DbContextOptionsBuilder builder)
        {
            if (sharedConnection is null)
                builder.UseNpgsql(ConnectionString, o => npgsql?.Invoke(o));
            else
                builder.UseNpgsql(sp.GetRequiredService<NpgsqlConnection>(), o => npgsql?.Invoke(o)); // the scope's shared connection
            if (interceptors.Length > 0)
                builder.AddInterceptors(interceptors);
        }

        if (sharedConnection is not null)
            services.AddScoped(sharedConnection);
        services.AddDbContext<AppDbContext>(Configure);
        services.AddDbContext<AuditDbContext>(Configure);
        services.AddWaybillOutbox<AppDbContext>();
        services.AddWaybillOutbox<AuditDbContext>();
        services.AddWaybillInbox<AppDbContext>();
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
    }

    public async Task<long> ScalarAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }

    public Task<long> OutboxCountAsync() => ScalarAsync("SELECT count(*) FROM waybill.outbox");
}

/// <summary>Captures log entries so tests can assert on them.</summary>
public sealed class LogSink : ILoggerProvider
{
    public ConcurrentQueue<(LogLevel Level, string Category, string Message)> Entries { get; } = new();

    public ILogger CreateLogger(string categoryName) => new Logger(this, categoryName);

    public void Dispose()
    {
    }

    private sealed class Logger(LogSink sink, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            sink.Entries.Enqueue((logLevel, category, formatter(state, exception)));
    }
}
