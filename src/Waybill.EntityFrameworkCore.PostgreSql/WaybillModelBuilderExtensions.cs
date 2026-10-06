using Waybill.EntityFrameworkCore;

namespace Microsoft.EntityFrameworkCore;

/// <summary>Maps Waybill's outbox into the application's model.</summary>
public static class WaybillModelBuilderExtensions
{
    /// <summary>
    /// Maps <c>waybill.outbox</c> so that enqueued messages are inserted by the same <c>SaveChanges</c>, in the same
    /// transaction, as the application's data. The table is excluded from the application's migrations: Waybill
    /// ships and applies its own (<see cref="WaybillSchema.MigrateAsync"/>). Call it from <c>OnModelCreating</c>.
    /// </summary>
    public static ModelBuilder MapWaybillOutbox(this ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        modelBuilder.Entity<OutboxRecord>(outbox =>
        {
            outbox.ToTable(WaybillSchema.OutboxTable, WaybillSchema.Name, table => table.ExcludeFromMigrations());
            outbox.HasKey(m => m.Id);
            outbox.Property(m => m.Id).HasColumnName("id").ValueGeneratedNever();
            outbox.Property(m => m.Type).HasColumnName("type");
            outbox.Property(m => m.Key).HasColumnName("key");
            outbox.Property(m => m.KeyHash).HasColumnName("key_hash");
            outbox.Property(m => m.Payload).HasColumnName("payload");
            outbox.Property(m => m.ContentType).HasColumnName("content_type");
            outbox.Property(m => m.Headers).HasColumnName("headers").HasColumnType("jsonb");
        });
        return modelBuilder;
    }
}
