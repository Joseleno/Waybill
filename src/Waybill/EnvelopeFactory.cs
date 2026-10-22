using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace Waybill;

internal static class EnvelopeFactory
{
    public const string JsonContentType = "application/json";

    public static MessageEnvelope Create<TMessage>(
        WaybillOptions options, TMessage message, string? key, string? correlationId, string? tenantId)
        where TMessage : notnull
    {
        ArgumentNullException.ThrowIfNull(message);
        if (options.MaxPayloadBytes <= 0)
            throw new InvalidOperationException("WaybillOptions.MaxPayloadBytes must be configured with a positive value.");
        EnsureShortString(correlationId, "correlation id");
        EnsureShortString(key, "aggregate key");
        EnsureShortString(tenantId, "tenant id");

        var messageType = options.GetMessageType(message.GetType());
        var payload = JsonSerializer.SerializeToUtf8Bytes(message, messageType.TypeInfo);
        if (payload.Length > options.MaxPayloadBytes)
            throw new InvalidOperationException(
                $"Message '{messageType.Name}' serializes to {payload.Length} bytes, above MaxPayloadBytes ({options.MaxPayloadBytes}). " +
                "Large payloads belong in a claim-check managed by the application.");

        var id = MessageId.NewV7();
        var keyHash = key is null ? KeyHash.Of(id) : KeyHash.Of(key);
        // After every check: a refused Enqueue opens nothing. The stored traceparent is the create span's, so what the
        // message causes later hangs from it; without one (nothing listens, or nobody samples the caller), the caller's.
        using var create = StartCreate(options, id, messageType.Name, key, tenantId);
        return new MessageEnvelope(id, messageType.Name, key, keyHash, payload, JsonContentType,
            Headers(create ?? Activity.Current, correlationId, tenantId));
    }

    // A short span in the caller's flow, child of whatever is current, in W3C whatever the process default; closing it
    // gives the caller its Current back. The message id goes in at creation, where a sampler can see it.
    private static Activity? StartCreate(WaybillOptions options, Guid id, string name, string? key, string? tenantId)
    {
        if (!WaybillActivity.Source.HasListeners())
            return null;
        List<KeyValuePair<string, object?>> tags =
        [
            new("messaging.operation.type", "create"),
            new("messaging.operation.name", "create"),
            new("messaging.message.id", id.ToString()),
            new("waybill.message.name", name),
        ];
        if (options.RecordKeyAndTenantInTraces)
        {
            if (key is not null)
                tags.Add(new("waybill.key", key));
            if (tenantId is not null)
                tags.Add(new("waybill.tenant_id", tenantId));
        }
        return WaybillActivity.Source.CreateActivity("create", ActivityKind.Producer, default(ActivityContext), tags, idFormat: ActivityIdFormat.W3C)?.Start();
    }

    /// <summary>
    /// Envelope fields that travel as broker properties or headers (message name, correlation id, aggregate key,
    /// tenant id) are limited to 255 UTF-8 bytes, AMQP's short string. Rejected up front: past the outbox, a value the
    /// broker cannot carry could only fail at every publish.
    /// </summary>
    internal const int MaxShortStringBytes = 255;

    internal static void EnsureShortString(string? value, string what)
    {
        if (value is not null && Encoding.UTF8.GetByteCount(value) > MaxShortStringBytes)
            throw new InvalidOperationException($"The {what} '{value[..Math.Min(value.Length, 40)]}…' is longer than {MaxShortStringBytes} UTF-8 bytes.");
    }

    // W3C trace context plus the optional ids, as a JSON object; null when there is nothing to carry.
    private static string? Headers(Activity? activity, string? correlationId, string? tenantId)
    {
        var traceParent = activity?.IdFormat == ActivityIdFormat.W3C ? activity.Id : null;
        var traceState = activity?.TraceStateString;
        if (traceParent is null && correlationId is null && tenantId is null)
            return null;

        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            WriteIfPresent(writer, "traceparent", traceParent);
            WriteIfPresent(writer, "tracestate", traceState);
            WriteIfPresent(writer, "correlation_id", correlationId);
            WriteIfPresent(writer, "tenant_id", tenantId);
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    private static void WriteIfPresent(Utf8JsonWriter writer, string name, string? value)
    {
        if (value is not null)
            writer.WriteString(name, value);
    }
}
