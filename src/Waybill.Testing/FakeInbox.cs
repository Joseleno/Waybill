using Microsoft.EntityFrameworkCore;
using Waybill.EntityFrameworkCore;

namespace Waybill.Testing;

/// <summary>What fake inboxes processed: the in-memory stand-in for the <c>waybill.inbox</c> table.</summary>
public sealed class FakeInboxMemory
{
    private readonly Lock _gate = new();
    private readonly List<(string Handler, Guid MessageId)> _processed = [];
    private readonly Dictionary<(string Handler, Guid MessageId), TaskCompletionSource> _inProgress = [];
    private int _duplicates;

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
        string handler, Guid messageId, Func<TContext, CancellationToken, Task> handle, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(handler);
        ArgumentNullException.ThrowIfNull(handle);
        EnvelopeFactory.EnsureShortString(handler, "inbox handler name");

        if (!await memory.TryReserveAsync(handler, messageId, cancellationToken).ConfigureAwait(false))
            return InboxResult.Duplicate;

        var processed = false;
        try
        {
            await handle(context, cancellationToken).ConfigureAwait(false);
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            processed = true;
        }
        catch
        {
            context.ChangeTracker.Clear();
            throw;
        }
        finally
        {
            memory.Release(handler, messageId, processed);
        }

        return InboxResult.Processed;
    }
}
