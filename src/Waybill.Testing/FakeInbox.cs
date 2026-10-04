using Microsoft.EntityFrameworkCore;
using Waybill.EntityFrameworkCore;

namespace Waybill.Testing;

/// <summary>What fake inboxes processed: the in-memory stand-in for the <c>waybill.inbox</c> table.</summary>
public sealed class FakeInboxMemory
{
    private readonly Lock _gate = new();
    private readonly List<(string Handler, Guid MessageId)> _processed = [];
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

    internal bool IsProcessed(string handler, Guid messageId)
    {
        lock (_gate)
        {
            if (!_processed.Contains((handler, messageId)))
                return false;
            _duplicates++;
            return true;
        }
    }

    internal void Record(string handler, Guid messageId)
    {
        lock (_gate)
            _processed.Add((handler, messageId));
    }
}

/// <summary>
/// In-memory <see cref="IInbox{TContext}"/> for testing consumer handlers without PostgreSQL. It runs the handler with
/// the given context and saves it, and treats a pair already in its <see cref="FakeInboxMemory"/> as a duplicate,
/// like the real inbox.
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

        if (memory.IsProcessed(handler, messageId))
            return InboxResult.Duplicate;

        try
        {
            await handle(context, cancellationToken).ConfigureAwait(false);
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            context.ChangeTracker.Clear();
            throw;
        }

        memory.Record(handler, messageId);
        return InboxResult.Processed;
    }
}
