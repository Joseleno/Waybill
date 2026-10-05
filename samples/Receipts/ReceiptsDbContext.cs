using Microsoft.EntityFrameworkCore;
using Waybill.EntityFrameworkCore;

namespace Receipts;

public sealed class Receipt
{
    public Guid Id { get; init; } = Guid.CreateVersion7();
    public Guid InvoiceId { get; init; }
    public decimal Amount { get; init; }
    public DateTimeOffset IssuedAt { get; init; }
}

public sealed class ReceiptsDbContext(DbContextOptions<ReceiptsDbContext> options) : DbContext(options)
{
    public DbSet<Receipt> Receipts => Set<Receipt>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Receipt>(receipt =>
        {
            receipt.ToTable("receipts");
            receipt.Property(r => r.Id).HasColumnName("id");
            receipt.Property(r => r.InvoiceId).HasColumnName("invoice_id");
            receipt.Property(r => r.Amount).HasColumnName("amount").HasPrecision(12, 2);
            receipt.Property(r => r.IssuedAt).HasColumnName("issued_at");
        });

        // This service also publishes (receipts.receipt-issued.v1), so it maps the outbox too.
        modelBuilder.AddWaybillOutbox();
    }
}

/// <summary>Lets `dotnet ef migrations add` build the context without running the app.</summary>
internal sealed class ReceiptsDbContextDesignTimeFactory : Microsoft.EntityFrameworkCore.Design.IDesignTimeDbContextFactory<ReceiptsDbContext>
{
    public ReceiptsDbContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<ReceiptsDbContext>().UseNpgsql("Host=localhost").Options);
}
