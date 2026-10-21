using Microsoft.EntityFrameworkCore;
using Waybill.EntityFrameworkCore;

namespace Waybill.Testing;

/// <summary>What fake inboxes processed: the in-memory stand-in for the <c>waybill.inbox</c> table.</summary>
public sealed class FakeInboxMemory
{
    private readonly Lock _gate = new();
    private readonly List<(string Handler, Guid MessageId)> _processed = [];
    private readonly Dictionary<(string Handler, Guid MessageId), TaskCompletionSource> _inProgress = [];
    private readonly Dictionary<(string Handler, string Key), (long Sequence, Guid MessageId)> _keys = [];
    private readonly Dictionary<(string Handler, string Key), TaskCompletionSource> _keysInProgress = [];
    private int _duplicates;
    private int _regressions;

    /// <summary>Pairs processed, in order.</summary>
    public IReadOnlyList<(string Handler, Guid MessageId)> Processed
    {
        get
        {
            lock (_gate)
                return [.. _processed];
        }
    }

    /// <summary>How many deliveries were recognized as duplicates.</summary>
    public int Duplicates => Volatile.Read(ref _duplicates);

    /// <summary>How many deliveries came back <see cref="OrderedInboxResult.Regressed"/>.</summary>
    public int Regressions => Volatile.Read(ref _regressions);

    /// <summary>Asserts that <paramref name="handler"/> processed <paramref name="messageId"/>.</summary>
    /// <exception cref="WaybillAssertionException">It was not processed.</exception>
    public void ShouldHaveProcessed(string handler, Guid messageId)
    {
        if (!Processed.Contains((handler, messageId)))
            throw new WaybillAssertionException(
                $"Expected handler '{handler}' to have processed message {messageId}. Processed: [{string.Join(", ", Processed.Select(p => $"{p.Handler}:{p.MessageId}"))}].");
    }

    // True when the caller now holds the pair and must Release it; false for a duplicate. Like the real inbox row, a
    // pair in progress makes a second delivery wait for the first to end.
    internal async Task<bool> TryReserveAsync(string handler, Guid messageId, CancellationToken cancellationToken)
    {
        while (true)
        {
            Task inProgress;
            lock (_gate)
            {
                if (_processed.Contains((handler, messageId)))
                {
                    _duplicates++;
                    return false;
                }
                if (!_inProgress.TryGetValue((handler, messageId), out var pending))
                {
                    _inProgress.Add((handler, messageId), new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
                    return true;
                }
                inProgress = pending.Task;
            }
            await inProgress.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    internal void Release(string handler, Guid messageId, bool processed)
    {
        TaskCompletionSource? pending;
        lock (_gate)
        {
            if (processed)
                _processed.Add((handler, messageId));
            _inProgress.Remove((handler, messageId), out pending);
        }
        pending?.SetResult();
    }

    // Holds the key for the caller, who must ReleaseKey it, and returns the last message the handler applied for it.
    // Like the real inbox_keys row, a delivery of a key in progress makes another of the same key wait for it to end.
    internal async Task<(long Sequence, Guid MessageId)?> LockKeyAsync(string handler, string key, CancellationToken cancellationToken)
    {
        while (true)
        {
            Task inProgress;
            lock (_gate)
            {
                if (!_keysInProgress.TryGetValue((handler, key), out var pending))
                {
                    _keysInProgress.Add((handler, key), new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
                    return _keys.TryGetValue((handler, key), out var last) ? last : null;
                }
                inProgress = pending.Task;
            }
            await inProgress.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    // Lets the key go; when the handler applied a message, the key moves to it.
    internal void ReleaseKey(string handler, string key, (long Sequence, Guid MessageId)? applied)
    {
        TaskCompletionSource? pending;
        lock (_gate)
        {
            if (applied is { } last)
                _keys[(handler, key)] = last;
            _keysInProgress.Remove((handler, key), out pending);
        }
        pending?.SetResult();
    }

    internal void CountRegression() => Interlocked.Increment(ref _regressions);

    internal void CountDuplicate()
    {
        lock (_gate)
            _duplicates++;
    }
}

/// <summary>
/// In-memory <see cref="IInbox{TContext}"/> for testing consumer handlers without PostgreSQL. It runs the handler with
/// the given context and saves it, and treats a pair already in its <see cref="FakeInboxMemory"/> as a duplicate,
/// like the real inbox; a delivery of a pair still in progress waits for the first to end.
/// </summary>
/// <remarks>
/// Limits: no database transaction (a handler failure clears the context's tracker but cannot undo a
/// <c>SaveChanges</c> the handler made itself), and no guard against writes through other contexts. Test those paths
/// against the real inbox.
/// </remarks>
public sealed class FakeInbox<TContext>(TContext context, FakeInboxMemory memory) : IInbox<TContext>
    where TContext : DbContext
{
    /// <summary>A fake with its own memory.</summary>
    public FakeInbox(TContext context)
        : this(context, new FakeInboxMemory())
    {
    }

    /// <summary>What this fake (and any other sharing its memory) processed.</summary>
    public FakeInboxMemory Memory => memory;

    /// <inheritdoc />
    public async Task<InboxResult> ProcessAsync(
        string handler, Guid messageId, Func<TContext, CancellationToken, Task> handle, CancellationToken cancellationToken = default) =>
        await ProcessCoreAsync(handler, messageId, null, handle, cancellationToken).ConfigureAwait(false) == OrderedInboxResult.Processed
            ? InboxResult.Processed
            : InboxResult.Duplicate;

    /// <inheritdoc />
    public Task<OrderedInboxResult> ProcessInOrderAsync(
        string handler, Guid messageId, MessageOrder? order, Func<TContext, CancellationToken, Task> handle, CancellationToken cancellationToken = default)
    {
        if (order is { Key: null })
            throw new ArgumentException(
                "default(MessageOrder) carries no key. Read the order from the message, or pass null for a message that is not ordered.", nameof(order));
        return ProcessCoreAsync(handler, messageId, order, handle, cancellationToken);
    }

    // The real inbox's order: the message id first, then the key, held until the handler ends, then the sequence.
    private async Task<OrderedInboxResult> ProcessCoreAsync(
        string handler, Guid messageId, MessageOrder? order, Func<TContext, CancellationToken, Task> handle, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(handler);
        ArgumentNullException.ThrowIfNull(handle);
        EnvelopeFactory.EnsureShortString(handler, "inbox handler name");

        if (!await memory.TryReserveAsync(handler, messageId, cancellationToken).ConfigureAwait(false))
            return OrderedInboxResult.Duplicate;

        var processed = false;
        (long Sequence, Guid MessageId)? applied = null;
        try
        {
            if (order is { } verified)
            {
                try
                {
                    var last = await memory.LockKeyAsync(handler, verified.Key, cancellationToken).ConfigureAwait(false);
                    if (last is { } previous && previous.Sequence >= verified.Sequence)
                    {
                        if (previous.Sequence == verified.Sequence && previous.MessageId == messageId)
                        {
                            memory.CountDuplicate();
                            return OrderedInboxResult.Duplicate;
                        }
                        memory.CountRegression();
                        return OrderedInboxResult.Regressed;
                    }

                    processed = await HandleAsync(handle, cancellationToken).ConfigureAwait(false);
                    applied = (verified.Sequence, messageId);
                }
                finally
                {
                    memory.ReleaseKey(handler, verified.Key, applied);
                }
            }
            else
            {
                processed = await HandleAsync(handle, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            memory.Release(handler, messageId, processed);
        }

        return OrderedInboxResult.Processed;
    }

    private async Task<bool> HandleAsync(Func<TContext, CancellationToken, Task> handle, CancellationToken cancellationToken)
    {
        try
        {
            await handle(context, cancellationToken).ConfigureAwait(false);
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch
        {
            context.ChangeTracker.Clear();
            throw;
        }
    }
}
