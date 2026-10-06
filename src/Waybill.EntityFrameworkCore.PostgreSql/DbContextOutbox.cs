using System.Transactions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Waybill.EntityFrameworkCore;

/// <summary>
/// Adds each enqueued message to the <see cref="DbContext"/>'s change tracker as an outbox record, so the context's
/// own unit of work is the single source of truth: the record is inserted by the next successful <c>SaveChanges</c>,
/// stays Added after a failed one, and is discarded together with the application's data by
/// <c>ChangeTracker.Clear()</c>, a rollback to a fresh context, or a pooled context being reset.
/// </summary>
internal sealed partial class DbContextOutbox<TContext>(
    TContext context, IOptions<WaybillOptions> options, ILogger<DbContextOutbox<TContext>> logger)
    : IOutbox<TContext>, IDisposable
    where TContext : DbContext
{
    private bool _mappingVerified;

    public Guid Enqueue<TMessage>(TMessage message, string? key = null, string? correlationId = null, string? tenantId = null)
        where TMessage : notnull
    {
        Verify(context, ref _mappingVerified);
        var envelope = EnvelopeFactory.Create(options.Value, message, key, correlationId, tenantId);
        context.Add(OutboxRecord.From(envelope));
        return envelope.Id;
    }

    // Fail at Enqueue, with what to do, instead of letting messages silently miss the table or commit apart from
    // the data.
    internal static void Verify(DbContext context, ref bool mappingVerified)
    {
        if (!mappingVerified)
        {
            if (context.Model.FindEntityType(typeof(OutboxRecord)) is null)
                throw new InvalidOperationException(
                    $"{context.GetType().Name} does not map the Waybill outbox. Call modelBuilder.MapWaybillOutbox() in OnModelCreating.");
            mappingVerified = true;
        }

        if (Transaction.Current is not null)
            throw new InvalidOperationException(
                "Waybill does not support TransactionScope. Use DbContext.Database.BeginTransaction, or let SaveChanges create the transaction.");

        if (context.Database.AutoTransactionBehavior == AutoTransactionBehavior.Never && context.Database.CurrentTransaction is null)
            throw new InvalidOperationException(
                "AutoTransactionBehavior.Never would let the outbox row commit apart from the data. Begin a transaction before enqueuing.");
    }

    public void Dispose()
    {
        int pending;
        try
        {
            pending = context.ChangeTracker.Entries<OutboxRecord>().Count(e => e.State == EntityState.Added);
        }
        catch (ObjectDisposedException)
        {
            return; // the context went first; its pending records went with it
        }

        if (pending == 0)
            return;

        if (options.Value.ThrowOnPendingMessagesAtDispose)
        {
            // The scope stops disposing at the first exception; release the context (idempotent, pool-aware) so
            // its connection and transaction are not left behind.
            context.Dispose();
            throw new InvalidOperationException(
                $"{pending} message(s) were enqueued on {typeof(TContext).Name} but never saved: SaveChanges was not called, or it failed.");
        }

        LogPendingMessages(logger, pending, typeof(TContext).Name);
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Error,
        Message = "{Count} message(s) were enqueued on {Context} but never saved: SaveChanges was not called, or it failed. They were discarded.")]
    private static partial void LogPendingMessages(ILogger logger, int count, string context);
}
