using System.Text.Json.Serialization;
using Waybill;

namespace Billing;

/// <summary>Published when an invoice is paid. Consumers depend on this shape, so it is versioned in its name.</summary>
public sealed record InvoicePaid(Guid InvoiceId, string Number, decimal Amount, DateTimeOffset PaidAt);

[JsonSerializable(typeof(InvoicePaid))]
internal sealed partial class BillingJson : JsonSerializerContext;

/// <summary>The messages this service publishes, registered in one place for the app and its tests.</summary>
public static class BillingMessages
{
    public const string InvoicePaid = "billing.invoice-paid.v1";

    public static void Register(WaybillOptions options)
    {
        options.MaxPayloadBytes = 16 * 1024;
        options.AddMessage(InvoicePaid, BillingJson.Default.InvoicePaid);
    }
}
