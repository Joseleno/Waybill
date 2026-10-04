namespace Waybill;

/// <summary>
/// Publishes outbox messages to a broker. Implemented by the transport packages (for example <c>Waybill.RabbitMQ</c>);
/// the dispatcher calls it with batches of claimed messages.
/// </summary>
/// <remarks>
/// A message counts as published only when the broker confirmed it (<see cref="PublishStatus.Confirmed"/>). An
/// exception, or cancellation of <c>cancellationToken</c> (the dispatcher's publish timeout), is treated as a
/// transport failure for the whole batch: every message is handed back to be published again, without spending an
/// attempt. Publishing is at-least-once, so the same message may reach the broker more than once.
/// </remarks>
public interface ITransport
{
    /// <summary>Publishes <paramref name="batch"/> and returns one result per message, in the same order.</summary>
    Task<IReadOnlyList<PublishResult>> PublishAsync(IReadOnlyList<OutgoingMessage> batch, CancellationToken cancellationToken);
}

/// <summary>A message taken from the outbox, ready to be published.</summary>
/// <param name="MessageId">Id fixed at enqueue time; it repeats on every redelivery, so consumers deduplicate on it.</param>
/// <param name="Name">Registered message name, for example <c>billing.invoice-paid.v1</c>.</param>
/// <param name="Key">Aggregate key, if any.</param>
/// <param name="Payload">Serialized message body.</param>
/// <param name="ContentType">Payload content type, for example <c>application/json</c>.</param>
/// <param name="Headers">Envelope headers: <c>traceparent</c>, <c>tracestate</c>, <c>correlation_id</c>, <c>tenant_id</c> when present.</param>
/// <param name="CreatedAt">When the message was written, by the database clock.</param>
public sealed record OutgoingMessage(
    Guid MessageId,
    string Name,
    string? Key,
    ReadOnlyMemory<byte> Payload,
    string ContentType,
    IReadOnlyDictionary<string, string> Headers,
    DateTimeOffset CreatedAt);

/// <summary>What happened to one message of a published batch.</summary>
public enum PublishStatus
{
    /// <summary>The broker confirmed the message. It is marked as published.</summary>
    Confirmed,

    /// <summary>Transport failure (connection, channel, missing confirmation). The message is handed back without spending an attempt.</summary>
    Retry,

    /// <summary>The broker returned the message as unroutable. Spends one of <c>MaxReturns</c> attempts, then goes to the DLQ.</summary>
    Returned,

    /// <summary>A defect of the message itself (for example, rejected for size). It goes to the outbox DLQ with the reason.</summary>
    Defect,
}

/// <summary>The result of publishing one message.</summary>
/// <param name="Status">The outcome.</param>
/// <param name="Reason">Why, for <see cref="PublishStatus.Returned"/> and <see cref="PublishStatus.Defect"/>; recorded in the DLQ.</param>
public readonly record struct PublishResult(PublishStatus Status, string? Reason = null)
{
    /// <summary>The broker confirmed the message.</summary>
    public static PublishResult Confirmed { get; } = new(PublishStatus.Confirmed);

    /// <summary>Transport failure; publish again without spending an attempt.</summary>
    public static PublishResult Retry { get; } = new(PublishStatus.Retry);
}
