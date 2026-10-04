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

        var messageType = options.GetMessageType(message.GetType());
        var payload = JsonSerializer.SerializeToUtf8Bytes(message, messageType.TypeInfo);
        if (payload.Length > options.MaxPayloadBytes)
            throw new InvalidOperationException(
                $"Message '{messageType.Name}' serializes to {payload.Length} bytes, above MaxPayloadBytes ({options.MaxPayloadBytes}). " +
                "Large payloads belong in a claim-check managed by the application.");

        var id = Guid.CreateVersion7();
        var keyHash = key is null ? KeyHash.Of(id) : KeyHash.Of(key);
        return new MessageEnvelope(id, messageType.Name, key, keyHash, payload, JsonContentType,
            Headers(Activity.Current, correlationId, tenantId));
    }

    /// <summary>
    /// Envelope fields that travel as broker properties (message name, correlation id) are limited to 255 UTF-8
    /// bytes, AMQP's short string. Rejected up front: past the outbox, a value the broker cannot carry could only
    /// fail at every publish.
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
