using System.Data;
using System.Data.Common;
using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;

namespace Waybill.EntityFrameworkCore;

internal sealed partial class DbContextInbox<TContext>(TContext context, ILogger<DbContextInbox<TContext>> logger, InboxMetrics metrics)
    : IInbox<TContext>
    where TContext : DbContext
{
    public async Task<InboxResult> ProcessAsync(
        string handler, Guid messageId, Func<TContext, CancellationToken, Task> handle, CancellationToken cancellationToken = default) =>
        await ProcessCoreAsync(handler, messageId, null, handle, cancellationToken).ConfigureAwait(false) switch
        {
            OrderedInboxResult.Processed => InboxResult.Processed,
            OrderedInboxResult.Duplicate => InboxResult.Duplicate,
            var other => throw new UnreachableException($"ProcessAsync verifies no sequence, yet the inbox returned {other}."),
        };

    public Task<OrderedInboxResult> ProcessInOrderAsync(
        string handler, Guid messageId, MessageOrder? order, Func<TContext, CancellationToken, Task> handle, CancellationToken cancellationToken = default)
    {
        if (order is { Key: null })
            throw new ArgumentException(
                "default(MessageOrder) carries no key. Read the order from the message, or pass null for a message that is not ordered.", nameof(order));
        return ProcessCoreAsync(handler, messageId, order, handle, cancellationToken);
    }

    private async Task<OrderedInboxResult> ProcessCoreAsync(
        string handler, Guid messageId, MessageOrder? order, Func<TContext, CancellationToken, Task> handle, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(handler);
        ArgumentNullException.ThrowIfNull(handle);
        EnvelopeFactory.EnsureShortString(handler, "inbox handler name");
        if (context.Database.CurrentTransaction is not null)
            throw new InvalidOperationException(
                $"{typeof(TContext).Name} already has a transaction. The inbox owns the transaction of the handler it runs: call ProcessAsync on a context without one.");

        // Inside the execution strategy, so a transient failure (including an ambiguous commit) retries the whole unit:
        // a commit that had in fact succeeded comes back as Duplicate, never as a second effect. What the sequence check
        // refused is logged once, after the strategy returns, however many times it ran.
        var attempt = 0;
        var (result, refused) = await context.Database.CreateExecutionStrategy().ExecuteAsync(async ct =>
        {
            if (attempt++ > 0)
                context.ChangeTracker.Clear();

            await using var transaction = await context.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct).ConfigureAwait(false);
            // A concurrent delivery of the same message waits here until the first transaction ends, then sees the row.
            var inserted = await context.Database.ExecuteSqlAsync(
                $"INSERT INTO waybill.inbox (handler, message_id) VALUES ({handler}, {messageId}) ON CONFLICT DO NOTHING", ct).ConfigureAwait(false);
            if (inserted == 0)
            {
                await transaction.RollbackAsync(ct).ConfigureAwait(false);
                return (OrderedInboxResult.Duplicate, (LastApplied?)null);
            }

            // Deduplication first, then the sequence: the other way round, a redelivery of an applied message would be a
            // regression. Before the handler, so deliveries of one key apply in the order they arrive.
            if (order is { } verified && await RefusedAsync(handler, messageId, verified, ct).ConfigureAwait(false) is { } last)
            {
                await transaction.RollbackAsync(ct).ConfigureAwait(false); // nothing recorded: a redelivery is refused again
                return (last.Sequence == verified.Sequence && last.MessageId == messageId ? OrderedInboxResult.Duplicate : OrderedInboxResult.Regressed, last);
            }

            using (InboxScope.Enter(context, transaction.GetDbTransaction(), handler))
            {
                try
                {
                    await handle(context, ct).ConfigureAwait(false);
                    await context.SaveChangesAsync(ct).ConfigureAwait(false);
                }
                catch
                {
                    // Rolled back with the transaction: nothing the handler prepared (outbox messages included) may
                    // linger in the tracker and be saved by a later SaveChanges.
                    context.ChangeTracker.Clear();
                    throw;
                }
            }

            await transaction.CommitAsync(ct).ConfigureAwait(false);
            return (OrderedInboxResult.Processed, null);
        }, cancellationToken).ConfigureAwait(false);

        if (refused is { } applied && order is { } received)
        {
            if (result == OrderedInboxResult.Regressed)
            {
                var regression = applied.Sequence == received.Sequence ? Regression.Reused : Regression.Lower;
                LogRegression(logger, handler, received.Key, received.Sequence, applied.Sequence, messageId, applied.MessageId, regression);
                metrics.RecordRegression(handler, regression == Regression.Reused ? "reused" : "lower");
            }
            else
                LogDuplicateBySequence(logger, handler, received.Key, received.Sequence, messageId);
        }
        return result;
    }

    // Moves the key to this message if its sequence comes after the last applied one; otherwise returns what was applied.
    // The ON CONFLICT locks the key's row even when its WHERE is false, until the commit: a concurrent delivery of the key
    // waits, then compares with what this one wrote. The read is a statement of its own, never a CTE of the upsert, which
    // would read the snapshot from before the wait.
    private async Task<LastApplied?> RefusedAsync(string handler, Guid messageId, MessageOrder order, CancellationToken ct)
    {
        var advanced = await context.Database.ExecuteSqlAsync($"""
            INSERT INTO waybill.inbox_keys AS k (handler, key, last_seq, last_message_id)
            VALUES ({handler}, {order.Key}, {order.Sequence}, {messageId})
            ON CONFLICT (handler, key) DO UPDATE SET last_seq = EXCLUDED.last_seq, last_message_id = EXCLUDED.last_message_id
            WHERE k.last_seq < EXCLUDED.last_seq
            """, ct).ConfigureAwait(false);
        if (advanced == 1)
            return null;
        return await context.Database.SqlQuery<LastApplied>($"""
            SELECT last_seq AS "Sequence", last_message_id AS "MessageId" FROM waybill.inbox_keys WHERE handler = {handler} AND key = {order.Key}
            """).SingleAsync(ct).ConfigureAwait(false);
    }

    [LoggerMessage(EventId = 50, Level = LogLevel.Warning,
        Message = "Handler {Handler} refused message {MessageId} of key {Key}: its sequence {Sequence} is not after {LastSequence}, the last one it applied (message {LastMessageId}). Regression: {Regression}. Nothing was applied; send it to the dead-letter exchange.")]
    private static partial void LogRegression(
        ILogger logger, string handler, string key, long sequence, long lastSequence, Guid messageId, Guid lastMessageId, Regression regression);

    [LoggerMessage(EventId = 51, Level = LogLevel.Debug,
        Message = "Handler {Handler} already applied message {MessageId} of key {Key} (sequence {Sequence}); its inbox record was gone, its sequence was not.")]
    private static partial void LogDuplicateBySequence(ILogger logger, string handler, string key, long sequence, Guid messageId);

    // The last message a handler applied for a key.
    internal sealed class LastApplied
    {
        public long Sequence { get; init; }
        public Guid MessageId { get; init; }
    }

    // Lower: older than the last applied message. Reused: the last applied sequence, on another message.
    private enum Regression
    {
        Lower,
        Reused,
    }
}

/// <summary>The inbox transaction in progress on this async flow, if any.</summary>
internal sealed class InboxScope : IDisposable
{
    private static readonly AsyncLocal<InboxScope?> CurrentScope = new();

    private readonly InboxScope? _previous;
    private volatile bool _ended;

    private InboxScope(DbContext context, DbTransaction transaction, string handler)
    {
        Context = context;
        Transaction = transaction;
        Handler = handler;
        _previous = CurrentScope.Value;
    }

    // A flow the handler forked keeps its copy of the AsyncLocal after the handler returns; an ended scope no longer
    // counts there.
    public static InboxScope? Current
    {
        get
        {
            var scope = CurrentScope.Value;
            while (scope is { _ended: true })
                scope = scope._previous;
            return scope;
        }
    }

    public DbContext Context { get; }
    public DbTransaction Transaction { get; }
    public string Handler { get; }

    public static InboxScope Enter(DbContext context, DbTransaction transaction, string handler) =>
        CurrentScope.Value = new InboxScope(context, transaction, handler);

    public void Dispose()
    {
        _ended = true;
        CurrentScope.Value = _previous;
    }
}

/// <summary>
/// While a handler runs inside the inbox, a <c>SaveChanges</c> on another instance of the context outside the inbox
/// transaction would commit on its own: the effect could survive a rollback, or apply twice. Fail loudly instead.
/// </summary>
internal sealed class InboxGuardInterceptor : SaveChangesInterceptor
{
    public static readonly InboxGuardInterceptor Instance = new();

    private InboxGuardInterceptor()
    {
    }

    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        Guard(eventData.Context);
        return result;
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        Guard(eventData.Context);
        return ValueTask.FromResult(result);
    }

    private static void Guard(DbContext? context)
    {
        if (context is null || InboxScope.Current is not { } scope || ReferenceEquals(context, scope.Context))
            return;
        if (context.Database.CurrentTransaction?.GetDbTransaction() == scope.Transaction)
            return; // shares the inbox connection and transaction: commits or rolls back with it

        throw new InvalidOperationException(
            $"A {context.GetType().Name} saved changes outside the inbox transaction while handler '{scope.Handler}' was running. " +
            "Write through the context ProcessAsync passes to the handler (or share its connection and transaction), so the effect commits or rolls back with the inbox record.");
    }
}
