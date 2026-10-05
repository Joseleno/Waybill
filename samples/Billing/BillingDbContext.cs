using Microsoft.EntityFrameworkCore;
using Waybill.EntityFrameworkCore;

namespace Billing;

public enum InvoiceStatus
{
    Open,
    Paid,
}

public sealed class Invoice
{
    public Guid Id { get; init; } = Guid.CreateVersion7();
    public required string Number { get; init; }
    public decimal Amount { get; init; }
    public InvoiceStatus Status { get; set; } = InvoiceStatus.Open;
    public DateTimeOffset? PaidAt { get; set; }
}

public sealed class BillingDbContext(DbContextOptions<BillingDbContext> options) : DbContext(options)
{
    public DbSet<Invoice> Invoices => Set<Invoice>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Invoice>(invoice =>
        {
            invoice.ToTable("invoices");
            invoice.Property(i => i.Id).HasColumnName("id");
            invoice.Property(i => i.Number).HasColumnName("number");
            invoice.Property(i => i.Amount).HasColumnName("amount").HasPrecision(12, 2);
            invoice.Property(i => i.Status).HasColumnName("status").HasConversion<string>();
            invoice.Property(i => i.PaidAt).HasColumnName("paid_at");
            invoice.HasIndex(i => i.Number).IsUnique();
        });

        // Maps waybill.outbox so Enqueue joins this context's SaveChanges; the table itself comes from
        // WaybillSchema.MigrateAsync, not from this application's migrations.
        modelBuilder.MapWaybillOutbox();
    }
}

/// <summary>Lets `dotnet ef migrations add` build the context without running the app.</summary>
internal sealed class BillingDbContextDesignTimeFactory : Microsoft.EntityFrameworkCore.Design.IDesignTimeDbContextFactory<BillingDbContext>
{
    public BillingDbContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<BillingDbContext>().UseNpgsql("Host=localhost").Options);
}
