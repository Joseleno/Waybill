namespace Waybill;

/// <summary>
/// Publishes outbox messages to a broker. Implemented by the transport packages (for example <c>Waybill.RabbitMQ</c>);
/// the dispatcher calls it with batches of claimed messages.
/// </summary>
/// <remarks>
/// <para>
/// A message counts as published only when the broker confirmed it (<see cref="PublishStatus.Confirmed"/>). An
/// exception, a result outside <see cref="PublishStatus"/>, or a call that has not returned by the dispatcher's
/// publish timeout is a transport failure for the whole batch: every message is handed back to be published again,
/// without spending an attempt. Publishing is at-least-once, so the same message may reach the broker more than once.
/// </para>
/// <para>
/// The dispatcher stops waiting at the timeout even if the call ignores <c>cancellationToken</c>, and starts its
/// next cycle. An implementation must therefore tolerate a previous, abandoned call still running when the next one
/// starts.
/// </para>
/// </remarks>
public interface ITransport
{
    /// <summary>Publishes <paramref name="batch"/> and returns one result per message, in the same order.</summary>
    Task<IReadOnlyList<PublishResult>> PublishAsync(IReadOnlyList<OutgoingMessage> batch, CancellationToken cancellationToken);
}

/// <summary>A message taken from the outbox, ready to be published.</summary>
public sealed class OutgoingMessage
{
    /// <summary>Id fixed at enqueue time; it repeats on every redelivery, so consumers deduplicate on it.</summary>
    public required Guid MessageId { get; init; }

    /// <summary>Registered message name, for example <c>billing.invoice-paid.v1</c>.</summary>
    public required string Name { get; init; }

    /// <summary>Aggregate key, if any.</summary>
    public string? Key { get; init; }

    /// <summary>Serialized message body.</summary>
    public required ReadOnlyMemory<byte> Payload { get; init; }

    /// <summary>Payload content type, for example <c>application/json</c>.</summary>
    public required string ContentType { get; init; }

    /// <summary>Envelope headers: <c>traceparent</c>, <c>tracestate</c>, <c>correlation_id</c>, <c>tenant_id</c> when present.</summary>
    public required IReadOnlyDictionary<string, string> Headers { get; init; }

    /// <summary>When the message was written, by the database clock.</summary>
    public required DateTimeOffset CreatedAt { get; init; }
}

/// <summary>What happened to one message of a published batch.</summary>
/// <remarks>There is deliberately no member with value 0: a <c>default</c> result is invalid and treated as <see cref="Retry"/>, never as confirmed.</remarks>
public enum PublishStatus
{
    /// <summary>The broker confirmed the message. It is marked as published.</summary>
    Confirmed = 1,

    /// <summary>Transport failure (connection, channel, missing confirmation). The message is handed back without spending an attempt.</summary>
    Retry = 2,

    /// <summary>The broker returned the message as unroutable. Spends one of <c>MaxReturns</c> attempts, then goes to the DLQ.</summary>
    Returned = 3,

    /// <summary>A defect of the message itself (for example, rejected for size). It goes to the outbox DLQ with the reason.</summary>
    Defect = 4,
}

/// <summary>Why a <see cref="PublishStatus.Retry"/> happened. The dispatcher reacts differently to each cause.</summary>
public enum TransportFailure
{
    /// <summary>Not stated by the transport. Treated like <see cref="Connection"/>.</summary>
    Unspecified = 0,

    /// <summary>The connection or the channel failed or closed. Opens the dispatcher's circuit breaker.</summary>
    Connection = 1,

    /// <summary>The broker did not confirm in time. Halves the batch; does not open the breaker.</summary>
    ConfirmTimeout = 2,

    /// <summary>The broker refused the message (nack), typically back-pressure. Halves the batch; does not open the breaker.</summary>
    Nacked = 3,
}

/// <summary>The result of publishing one message.</summary>
/// <param name="Status">The outcome.</param>
/// <param name="Reason">Why: logged for <see cref="PublishStatus.Retry"/>, recorded in the DLQ for <see cref="PublishStatus.Returned"/> and <see cref="PublishStatus.Defect"/>.</param>
public readonly record struct PublishResult(PublishStatus Status, string? Reason = null)
{
    /// <summary>For <see cref="PublishStatus.Retry"/>: what failed.</summary>
    public TransportFailure Failure { get; init; }

    /// <summary>The broker confirmed the message.</summary>
    public static PublishResult Confirmed { get; } = new(PublishStatus.Confirmed);

    /// <summary>Transport failure of unstated cause; publish again without spending an attempt.</summary>
    public static PublishResult Retry { get; } = new(PublishStatus.Retry);

    /// <summary>Transport failure of a known cause; publish again without spending an attempt.</summary>
    public static PublishResult RetryAfter(TransportFailure failure, string? reason = null) => new(PublishStatus.Retry, reason) { Failure = failure };

    /// <summary>Unroutable; spends one of the return attempts.</summary>
    public static PublishResult Returned(string reason) => new(PublishStatus.Returned, reason);

    /// <summary>Defect of the message; goes to the DLQ with <paramref name="reason"/>.</summary>
    public static PublishResult Defect(string reason) => new(PublishStatus.Defect, reason);
}
