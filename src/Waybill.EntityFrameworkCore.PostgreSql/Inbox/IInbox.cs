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
}
