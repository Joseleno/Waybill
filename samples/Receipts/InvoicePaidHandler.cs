using Waybill.EntityFrameworkCore;

namespace Receipts;

/// <summary>Issues the receipt for a paid invoice and publishes <see cref="ReceiptIssued"/> in the same transaction.</summary>
public sealed class ReceiptIssuer(IOutbox<ReceiptsDbContext> outbox)
{
    public Task IssueAsync(ReceiptsDbContext db, InvoicePaid paid, CancellationToken cancellationToken)
    {
        var receipt = new Receipt { InvoiceId = paid.InvoiceId, Amount = paid.Amount, IssuedAt = DateTimeOffset.UtcNow };
        db.Receipts.Add(receipt);
        outbox.Enqueue(new ReceiptIssued(receipt.Id, receipt.InvoiceId, receipt.Amount, receipt.IssuedAt), key: paid.InvoiceId.ToString());
        // No SaveChanges here: the inbox saves and commits once this returns, so the receipt, the event and the inbox
        // record commit together. `db` is the scope's context, the same instance the injected outbox enqueues into.
        return Task.CompletedTask;
    }
}

/// <summary>
/// Handles billing.invoice-paid.v1 once per message: the inbox records the message id and runs the issuer in the same
/// transaction, so a redelivery returns <see cref="InboxResult.Duplicate"/> without issuing a second receipt.
/// </summary>
public sealed class InvoicePaidHandler(IInbox<ReceiptsDbContext> inbox, ReceiptIssuer issuer)
{
    /// <summary>Stable and unique per handler: it is half of the inbox key.</summary>
    public const string Name = "receipts.issue-receipt";

    public Task<InboxResult> HandleAsync(Guid messageId, InvoicePaid paid, CancellationToken cancellationToken) =>
        inbox.ProcessAsync(Name, messageId, (db, ct) => issuer.IssueAsync(db, paid, ct), cancellationToken);
}
