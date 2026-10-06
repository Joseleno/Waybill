using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Waybill.EntityFrameworkCore.Schema;

/// <summary>
/// The full Waybill schema. Used only to create and upgrade the tables through the package's own migrations;
/// the application never queries through it.
/// </summary>
/// <remarks>
/// New migration: <c>dotnet ef migrations add &lt;Name&gt; --project src/Waybill.EntityFrameworkCore.PostgreSql
/// --context WaybillSchemaContext --output-dir Schema/Migrations --namespace Waybill.EntityFrameworkCore.Schema.Migrations</c>,
/// then make the generated class <c>internal</c> (migrations are not public API) and keep the model snapshot in
/// <c>Schema/Migrations</c>. Every migration must be compatible with rows still pending in the outbox.
/// </remarks>
internal sealed class WaybillSchemaContext(DbContextOptions<WaybillSchemaContext> options) : DbContext(options)
{
    public DbSet<OutboxRow> Outbox => Set<OutboxRow>();
    public DbSet<InboxRow> Inbox => Set<InboxRow>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(WaybillSchema.Name);

        modelBuilder.Entity<OutboxRow>(outbox =>
        {
            outbox.ToTable(WaybillSchema.OutboxTable, table => table.HasCheckConstraint(
                "ck_outbox_status", "status IN ('pending', 'claimed', 'published', 'dlq')"));
            outbox.HasKey(m => m.Id).HasName("pk_outbox");
            outbox.Property(m => m.Id).HasColumnName("id").ValueGeneratedNever();
            outbox.Property(m => m.Type).HasColumnName("type");
            outbox.Property(m => m.Key).HasColumnName("key");
            outbox.Property(m => m.KeyHash).HasColumnName("key_hash");
            outbox.Property(m => m.Sequence).HasColumnName("sequence");
            outbox.Property(m => m.Payload).HasColumnName("payload");
            outbox.Property(m => m.ContentType).HasColumnName("content_type");
            outbox.Property(m => m.Headers).HasColumnName("headers").HasColumnType("jsonb");
            outbox.Property(m => m.Status).HasColumnName("status").HasDefaultValue("pending");
            outbox.Property(m => m.Owner).HasColumnName("owner");
            outbox.Property(m => m.Fence).HasColumnName("fence").HasDefaultValue(0L);
            outbox.Property(m => m.Attempts).HasColumnName("attempts").HasDefaultValue(0);
            outbox.Property(m => m.LeaseUntil).HasColumnName("lease_until");
            outbox.Property(m => m.CreatedAt).HasColumnName("created_at").HasDefaultValueSql("clock_timestamp()");
            outbox.Property(m => m.PublishedAt).HasColumnName("published_at");
            outbox.Property(m => m.DlqReason).HasColumnName("dlq_reason");

            // The dispatcher's claim walks this index in id (UUIDv7) order; it only holds rows still in flight.
            outbox.HasIndex(m => m.Id).HasDatabaseName("ix_outbox_claimable").HasFilter("status IN ('pending', 'claimed')");
        });

        modelBuilder.Entity<InboxRow>(inbox =>
        {
            inbox.ToTable(WaybillSchema.InboxTable);
            inbox.HasKey(m => new { m.Handler, m.MessageId }).HasName("pk_inbox");
            inbox.Property(m => m.Handler).HasColumnName("handler");
            inbox.Property(m => m.MessageId).HasColumnName("message_id");
            inbox.Property(m => m.ProcessedAt).HasColumnName("processed_at").HasDefaultValueSql("clock_timestamp()");

            // Retention deletes by age; the message_id may come from another system and not be a UUIDv7 (ADR 0004).
            inbox.HasIndex(m => m.ProcessedAt).HasDatabaseName("ix_inbox_processed_at");
        });
    }

    public static DbContextOptions<WaybillSchemaContext> Options(string connectionString) =>
        new DbContextOptionsBuilder<WaybillSchemaContext>()
            .UseNpgsql(connectionString, npgsql => npgsql.MigrationsHistoryTable(WaybillSchema.MigrationsHistoryTable, WaybillSchema.Name))
            .Options;
}

internal sealed class OutboxRow
{
    public Guid Id { get; set; }
    public required string Type { get; set; }
    public string? Key { get; set; }
    public int KeyHash { get; set; }
    public long? Sequence { get; set; }
    public required byte[] Payload { get; set; }
    public required string ContentType { get; set; }
    public string? Headers { get; set; }
    public required string Status { get; set; }
    public string? Owner { get; set; }
    public long Fence { get; set; }
    public int Attempts { get; set; }
    public DateTimeOffset? LeaseUntil { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? PublishedAt { get; set; }
    public string? DlqReason { get; set; }
}

internal sealed class InboxRow
{
    public required string Handler { get; set; }
    public Guid MessageId { get; set; }
    public DateTimeOffset ProcessedAt { get; set; }
}

/// <summary>Lets <c>dotnet ef migrations add</c> build the schema context; no database is touched.</summary>
internal sealed class WaybillSchemaContextDesignTimeFactory : IDesignTimeDbContextFactory<WaybillSchemaContext>
{
    public WaybillSchemaContext CreateDbContext(string[] args) => new(WaybillSchemaContext.Options("Host=localhost"));
}
