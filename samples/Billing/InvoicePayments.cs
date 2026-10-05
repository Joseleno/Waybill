using Microsoft.EntityFrameworkCore;
using Waybill.EntityFrameworkCore;

namespace Billing;

public enum PaymentResult
{
    Paid,
    NotFound,
    AlreadyPaid,
}

/// <summary>Pays an invoice and records <see cref="InvoicePaid"/> in the same transaction.</summary>
public sealed class InvoicePayments(BillingDbContext db, IOutbox<BillingDbContext> outbox)
{
    public async Task<PaymentResult> PayAsync(Guid invoiceId, CancellationToken cancellationToken)
    {
        var invoice = await db.Invoices.FindAsync([invoiceId], cancellationToken);
        if (invoice is null)
            return PaymentResult.NotFound;
        if (invoice.Status == InvoiceStatus.Paid)
            return PaymentResult.AlreadyPaid;

        invoice.Status = InvoiceStatus.Paid;
        invoice.PaidAt = DateTimeOffset.UtcNow;

        // The event joins the SaveChanges below: it exists if and only if the payment commits. The key names the
        // aggregate; it travels with the message (header `waybill-key`).
        outbox.Enqueue(new InvoicePaid(invoice.Id, invoice.Number, invoice.Amount, invoice.PaidAt.Value), key: invoice.Id.ToString());
        try
        {
            await db.SaveChangesAsync(cancellationToken);
            return PaymentResult.Paid;
        }
        catch (DbUpdateConcurrencyException)
        {
            // Another request paid it first. Nothing was written; drop this attempt, the enqueued event included.
            db.ChangeTracker.Clear();
            return PaymentResult.AlreadyPaid;
        }
    }
}
