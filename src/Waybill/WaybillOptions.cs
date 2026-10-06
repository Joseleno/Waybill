using System.Text.Json.Serialization.Metadata;

namespace Waybill;

/// <summary>Configuration for Waybill. Set it up with <c>services.AddWaybill(options => ...)</c>.</summary>
public sealed class WaybillOptions
{
    private readonly Dictionary<Type, MessageType> _byType = [];
    private readonly Dictionary<string, MessageType> _byName = new(StringComparer.Ordinal);

    /// <summary>
    /// Maximum serialized payload size, in bytes. Required. Keep it below the broker's maximum message size;
    /// larger payloads belong in a claim-check managed by the application.
    /// </summary>
    public int MaxPayloadBytes { get; set; }

    /// <summary>
    /// When <see langword="true"/>, ending the scope with enqueued messages that were never saved throws.
    /// The default logs an error instead, so that an exception thrown by the application before <c>SaveChanges</c>
    /// is not hidden by Waybill's.
    /// </summary>
    public bool ThrowOnPendingMessagesAtDispose { get; set; }

    /// <summary>
    /// Publishes the messages of each aggregate key in commit order (v0.2, in progress). Off by default: ordering costs
    /// throughput, and messages without a key are never ordered. Every dispatcher must agree; the setting is kept in the
    /// database and checked at startup.
    /// </summary>
    public bool OrderByKey { get; set; }

    /// <summary>
    /// Registers a message type under a stable name. The name travels in the envelope and is the contract with
    /// consumers, so renaming or moving the class does not change it. Serialization uses the source-generated
    /// <paramref name="typeInfo"/>.
    /// </summary>
    /// <param name="name">Stable message name, for example <c>billing.invoice-paid.v1</c>.</param>
    /// <param name="typeInfo">Source-generated metadata, for example <c>AppJson.Default.InvoicePaid</c>.</param>
    /// <exception cref="InvalidOperationException">The name or the type is already registered.</exception>
    public WaybillOptions AddMessage<TMessage>(string name, JsonTypeInfo<TMessage> typeInfo)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(typeInfo);
        EnvelopeFactory.EnsureShortString(name, "message name"); // it becomes the routing key and the AMQP type

        if (_byName.TryGetValue(name, out var existingName))
            throw new InvalidOperationException($"Message name '{name}' is already registered for {existingName.ClrType}.");
        if (_byType.TryGetValue(typeof(TMessage), out var existingType))
            throw new InvalidOperationException($"Message type {typeof(TMessage)} is already registered as '{existingType.Name}'.");

        var messageType = new MessageType(name, typeof(TMessage), typeInfo);
        _byName.Add(name, messageType);
        _byType.Add(typeof(TMessage), messageType);
        return this;
    }

    internal MessageType GetMessageType(Type clrType) =>
        _byType.TryGetValue(clrType, out var messageType)
            ? messageType
            : throw new InvalidOperationException(
                $"Message type {clrType} is not registered. Register it with options.AddMessage(\"<stable-name>\", <JsonSerializerContext>.Default.{clrType.Name}).");
}

internal sealed record MessageType(string Name, Type ClrType, JsonTypeInfo TypeInfo);
