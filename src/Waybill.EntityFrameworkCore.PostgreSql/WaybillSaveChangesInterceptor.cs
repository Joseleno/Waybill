using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Waybill.EntityFrameworkCore;

/// <summary>
/// Adds the context's pending outbox records to the unit of work right before it is saved, so they are inserted in
/// the same batch and transaction as the application's data.
/// </summary>
/// <remarks>
/// Deduplication is by reference: a record already tracked by the context is not added again, so a
/// <c>SaveChanges</c> that fails and is repeated in the same transaction still inserts one row per message. Records
/// stay tracked after the save (they become Unchanged when changes are accepted), which keeps the
/// <c>SaveChanges(acceptAllChangesOnSuccess: false)</c> + retry pattern correct: a retried save inserts them again
/// together with the application's data. Only the pending list is cleared on success, and nothing on failure.
/// </remarks>
internal sealed class WaybillSaveChangesInterceptor : SaveChangesInterceptor
{
    public static readonly WaybillSaveChangesInterceptor Instance = new();

    private WaybillSaveChangesInterceptor()
    {
    }

    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        AttachPending(eventData.Context);
        return result;
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        AttachPending(eventData.Context);
        return ValueTask.FromResult(result);
    }

    public override int SavedChanges(SaveChangesCompletedEventData eventData, int result)
    {
        ReleasePending(eventData.Context);
        return result;
    }

    public override ValueTask<int> SavedChangesAsync(
        SaveChangesCompletedEventData eventData, int result, CancellationToken cancellationToken = default)
    {
        ReleasePending(eventData.Context);
        return ValueTask.FromResult(result);
    }

    private static void AttachPending(DbContext? context)
    {
        if (context is null || OutboxBuffer.Find(context) is not { Pending.Count: > 0 } buffer)
            return;

        foreach (var record in buffer.Pending)
        {
            if (context.Entry(record).State == EntityState.Detached)
                context.Add(record);
        }
    }

    private static void ReleasePending(DbContext? context)
    {
        if (context is not null)
            OutboxBuffer.Find(context)?.Clear();
    }
}
