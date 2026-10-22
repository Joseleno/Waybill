using Microsoft.EntityFrameworkCore;

namespace Waybill.EntityFrameworkCore;

/// <summary>How <see cref="IInbox{TContext}.ProcessAsync"/> ended. Either way the message may be acknowledged.</summary>
public enum InboxResult
{
    /// <summary>First delivery for this handler: the handler ran and its effect committed with the inbox row.</summary>
    Processed = 1,

    /// <summary>
    /// Already processed by this handler: nothing ran, nothing changed. Also the result when this very call committed
    /// but the commit's acknowledgement was lost and the execution strategy retried: the effect is applied, once.
    /// </summary>
    Duplicate = 2,
}

/// <summary>How <see cref="IInbox{TContext}.ProcessInOrderAsync"/> ended.</summary>
/// <remarks>There is deliberately no member with value 0: a <c>default</c> result is invalid.</remarks>
public enum OrderedInboxResult
{
    /// <summary>First delivery for this handler, in order for its key: the handler ran and its effect committed. Acknowledge it.</summary>
    Processed = 1,

    /// <summary>Already processed by this handler: nothing ran, nothing changed. Acknowledge it.</summary>
    Duplicate = 2,

    /// <summary>
    /// Not applied: its <c>sequence</c> is not after the last one this handler applied for its key. Nothing ran, nothing
    /// was recorded, and a redelivery comes back <see cref="Regressed"/> again. Do not acknowledge it as processed:
    /// send it to the queue's dead-letter exchange (with RabbitMQ, <c>basic.nack</c> without requeue). The reason is in
    /// a warning log (EventId 50) naming the handler, the key, both sequences and both message ids. It may already have
    /// been applied: a late copy of an applied message, once inbox retention deleted its record, comes back this way.
    /// </summary>
    Regressed = 3,
}

/// <summary>
/// Applies a message's effect on the consumer's database once per handler, however many times the broker delivers
/// it. Wrap the handler with it inside whatever consumer the application uses; acknowledge after it returns.
/// </summary>
/// <typeparam name="TContext">The consumer's <see cref="DbContext"/>; its database holds the <c>waybill</c> schema.</typeparam>
public interface IInbox<TContext>
    where TContext : DbContext
{
    /// <summary>
    /// In one <c>READ COMMITTED</c> transaction on the scope's <typeparamref name="TContext"/>: records
    /// <c>(handler, messageId)</c>, runs <paramref name="handle"/> with that same context, saves and commits. If the pair
    /// is already recorded, nothing runs and the result is <see cref="InboxResult.Duplicate"/>. If
    /// <paramref name="handle"/> throws, everything rolls back and the exception propagates, so the message comes back
    /// and is processed again.
    /// </summary>
    /// <param name="handler">Stable name of the handler, unique among handlers (up to 255 UTF-8 bytes). Two handlers of the same message each apply.</param>
    /// <param name="messageId">The Waybill message id from the envelope (for RabbitMQ: <c>BasicProperties.MessageId</c>).</param>
    /// <param name="handle">The effect. Write through the context it receives; messages enqueued on the context's outbox join the same commit.</param>
    /// <param name="cancellationToken">Cancels the processing; the transaction rolls back.</param>
    /// <exception cref="InvalidOperationException">The context already has a transaction (the inbox owns it), or the handler wrote through another context outside the inbox transaction.</exception>
    /// <remarks>
    /// A second delivery of a message still being processed waits on the first transaction's inbox row until it
    /// commits or rolls back. Keep handlers well under the broker's delivery timeout (RabbitMQ <c>consumer_timeout</c>,
    /// 30 minutes by default): a delivery held unacknowledged past it closes the channel.
    /// </remarks>
    Task<InboxResult> ProcessAsync(
        string handler, Guid messageId, Func<TContext, CancellationToken, Task> handle, CancellationToken cancellationToken = default);

    /// <summary>
    /// Like <see cref="ProcessAsync"/>, and detects regressions: never applies, for a key, a message whose
    /// <c>sequence</c> is not after the last one this handler applied for that key. It detects; it never reorders nor
    /// holds a message back, and a gap is not an error (a consumer of some of a key's message types sees gaps).
    /// </summary>
    /// <param name="handler">Stable name of the handler, unique among handlers (up to 255 UTF-8 bytes). Sequences are tracked per handler: give one name to every message type of a key whose order matters.</param>
    /// <param name="messageId">The Waybill message id from the envelope.</param>
    /// <param name="order">
    /// The message's key and sequence (for RabbitMQ: <c>GetWaybillOrder</c>), or null for a message that is not
    /// ordered (no key, or written with ordering off), which is only deduplicated.
    /// </param>
    /// <param name="handle">The effect. Write through the context it receives; messages enqueued on the context's outbox join the same commit.</param>
    /// <param name="cancellationToken">Cancels the processing; the transaction rolls back.</param>
    /// <exception cref="ArgumentException"><paramref name="order"/> is <c>default</c>, which carries no key.</exception>
    /// <exception cref="InvalidOperationException">The context already has a transaction (the inbox owns it), or the handler wrote through another context outside the inbox transaction.</exception>
    /// <remarks>
    /// <para>
    /// In the transaction of <see cref="ProcessAsync"/>, after recording <c>(handler, messageId)</c> and before running
    /// <paramref name="handle"/>: deduplication first, then the sequence, so a redelivery of an applied message is a
    /// <see cref="OrderedInboxResult.Duplicate"/>, never a regression. A sequence equal to the last applied one is a
    /// duplicate when the message id is that message's too, and a regression otherwise (a producer restored from a
    /// backup, two producers behind one handler name).
    /// </para>
    /// <para>
    /// The key's row stays locked until the commit: deliveries of one key to one handler are applied one at a time,
    /// and a slow handler holds its key back. The wait for that lock counts against the command timeout (30 s by
    /// default with Npgsql: the call throws, and is retried as a whole only under <c>EnableRetryOnFailure</c>) and against the
    /// broker's delivery timeout.
    /// Sequences are counted per key in the producer's database: a handler verifies the keys of one producer.
    /// </para>
    /// <para>
    /// The first arrival of each message follows commit order only if the queue has one active consumer processing one
    /// delivery at a time. With competing consumers nothing is applied out of order, but a valid message may come back
    /// <see cref="OrderedInboxResult.Regressed"/>.
    /// </para>
    /// </remarks>
    Task<OrderedInboxResult> ProcessInOrderAsync(
        string handler, Guid messageId, MessageOrder? order, Func<TContext, CancellationToken, Task> handle, CancellationToken cancellationToken = default);
}
