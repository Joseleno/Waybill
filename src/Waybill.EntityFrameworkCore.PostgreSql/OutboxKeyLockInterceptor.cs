using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Waybill.EntityFrameworkCore;

/// <summary>
/// Hands the numbering trigger the keys it must lock in one order (ADR 0008). While ordering by key is on, each keyed
/// row locks its key's counter until the transaction commits, at its own INSERT, and EF inserts in id order, not key
/// order: two transactions enqueuing K1 and K2 in opposite orders would wait on each other. So every keyed row added to a
/// <c>SaveChanges</c> with two or more distinct keys carries all of them, and the trigger locks them, sorted, at the first
/// row. The list rides on the rows, not on the session: no I/O here, no state between calls, nothing a savepoint, a
/// retry of the execution strategy or a pooled context could leave behind. With ordering off the trigger drops it.
/// </summary>
/// <remarks>
/// Runs once per <c>SaveChanges</c>, before EF detects changes and outside the execution strategy, so a retried save
/// keeps the lists already set. It only fills a column of rows already in the change tracker, which stays the single
/// source of truth (ADR 0002): a row added after it runs is still saved, only without the list.
/// </remarks>
internal sealed class OutboxKeyLockInterceptor : SaveChangesInterceptor
{
    public static readonly OutboxKeyLockInterceptor Instance = new();

    private OutboxKeyLockInterceptor()
    {
    }

    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        if (eventData.Context is { } context)
            SetLockKeys(context);
        return result;
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        if (eventData.Context is { } context)
            SetLockKeys(context);
        return ValueTask.FromResult(result);
    }

    internal static void SetLockKeys(DbContext context)
    {
        if (context.Model.FindEntityType(typeof(OutboxRecord)) is null)
            return;

        // Outbox rows are Added by Enqueue itself; finding them needs no DetectChanges, which SaveChanges runs right after.
        List<OutboxRecord> keyed;
        var autoDetect = context.ChangeTracker.AutoDetectChangesEnabled;
        context.ChangeTracker.AutoDetectChangesEnabled = false;
        try
        {
            keyed = context.ChangeTracker.Entries<OutboxRecord>()
                .Where(e => e.State == EntityState.Added && e.Entity.Key is not null)
                .Select(e => e.Entity)
                .ToList();
        }
        finally
        {
            context.ChangeTracker.AutoDetectChangesEnabled = autoDetect;
        }
        var keys = keyed.Select(r => r.Key!).Distinct(StringComparer.Ordinal).ToArray();
        var lockKeys = keys.Length >= 2 ? keys : null; // one key needs no order; the trigger sorts
        foreach (var record in keyed)
            record.LockKeys = lockKeys;
    }
}
