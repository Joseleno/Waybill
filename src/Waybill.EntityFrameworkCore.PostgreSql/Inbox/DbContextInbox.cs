using System.Data;
using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Storage;

namespace Waybill.EntityFrameworkCore;

internal sealed class DbContextInbox<TContext>(TContext context) : IInbox<TContext>
    where TContext : DbContext
{
    public async Task<InboxResult> ProcessAsync(
        string handler, Guid messageId, Func<TContext, CancellationToken, Task> handle, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(handler);
        ArgumentNullException.ThrowIfNull(handle);
        EnvelopeFactory.EnsureShortString(handler, "inbox handler name");
        if (context.Database.CurrentTransaction is not null)
            throw new InvalidOperationException(
                $"{typeof(TContext).Name} already has a transaction. The inbox owns the transaction of the handler it runs: call ProcessAsync on a context without one.");

        // Inside the execution strategy, so a transient failure (including an ambiguous commit) retries the whole unit:
        // a commit that had in fact succeeded comes back as Duplicate, never as a second effect.
        var attempt = 0;
        return await context.Database.CreateExecutionStrategy().ExecuteAsync(async ct =>
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
                return InboxResult.Duplicate;
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
            return InboxResult.Processed;
        }, cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>The inbox transaction in progress on this async flow, if any.</summary>
internal sealed class InboxScope : IDisposable
{
    private static readonly AsyncLocal<InboxScope?> CurrentScope = new();

    private readonly InboxScope? _previous;

    private InboxScope(DbContext context, DbTransaction transaction, string handler)
    {
        Context = context;
        Transaction = transaction;
        Handler = handler;
        _previous = CurrentScope.Value;
    }

    public static InboxScope? Current => CurrentScope.Value;

    public DbContext Context { get; }
    public DbTransaction Transaction { get; }
    public string Handler { get; }

    public static InboxScope Enter(DbContext context, DbTransaction transaction, string handler) =>
        CurrentScope.Value = new InboxScope(context, transaction, handler);

    public void Dispose() => CurrentScope.Value = _previous;
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
