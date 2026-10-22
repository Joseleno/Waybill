using Waybill;
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
/// Handles billing.invoice-paid.v1 once per message, and in order per invoice. The inbox records the message id, checks
/// that the message comes after the last one applied for its invoice, and runs the issuer, all in one transaction. A
/// redelivery returns <see cref="OrderedInboxResult.Duplicate"/> without issuing a second receipt; a message older than
/// one already applied returns <see cref="OrderedInboxResult.Regressed"/>, and the consumer sends it to the dead-letter
/// queue.
/// </summary>
public sealed class InvoicePaidHandler(IInbox<ReceiptsDbContext> inbox, ReceiptIssuer issuer)
{
    /// <summary>Stable and unique per handler: it is half of the inbox key, and sequences are tracked per handler.</summary>
    public const string Name = "receipts.issue-receipt";

    /// <param name="order">The invoice's key and the message's sequence, from the headers; null for a message that is not ordered.</param>
    public Task<OrderedInboxResult> HandleAsync(Guid messageId, MessageOrder? order, InvoicePaid paid, CancellationToken cancellationToken) =>
        inbox.ProcessInOrderAsync(Name, messageId, order, (db, ct) => issuer.IssueAsync(db, paid, ct), cancellationToken);
}
