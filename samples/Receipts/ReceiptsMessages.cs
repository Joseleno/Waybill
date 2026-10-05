using System.Text.Json.Serialization;
using Waybill;

namespace Receipts;

/// <summary>
/// What this service reads from billing.invoice-paid.v1. The consumer owns its copy of the contract: it depends on
/// the message's shape, not on the producer's code.
/// </summary>
public sealed record InvoicePaid(Guid InvoiceId, string Number, decimal Amount, DateTimeOffset PaidAt);

/// <summary>Published when a receipt is issued for a paid invoice.</summary>
public sealed record ReceiptIssued(Guid ReceiptId, Guid InvoiceId, decimal Amount, DateTimeOffset IssuedAt);

[JsonSerializable(typeof(InvoicePaid))]
[JsonSerializable(typeof(ReceiptIssued))]
internal sealed partial class ReceiptsJson : JsonSerializerContext;

/// <summary>The messages this service publishes, registered in one place for the app and its tests.</summary>
public static class ReceiptsMessages
{
    public const string ReceiptIssued = "receipts.receipt-issued.v1";

    public static void Register(WaybillOptions options)
    {
        // Required: the largest payload this service may enqueue. Enqueue refuses anything bigger, before it reaches
        // the table or the broker. Size it to your messages and below the broker's own limit.
        options.MaxPayloadBytes = 16 * 1024;
        options.AddMessage(ReceiptIssued, ReceiptsJson.Default.ReceiptIssued);
    }
}
