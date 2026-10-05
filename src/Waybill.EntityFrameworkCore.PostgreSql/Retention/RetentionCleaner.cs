using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Waybill.EntityFrameworkCore.Retention;

/// <summary>What one cleanup pass deleted.</summary>
internal readonly record struct RetentionPass(int OutboxDeleted, int InboxDeleted);

/// <summary>One cleanup pass: deletes, batch by batch, what is past the retention in the outbox and the inbox.</summary>
internal sealed partial class RetentionCleaner(RetentionStore store, IOptions<WaybillRetentionOptions> options, ILogger<RetentionCleaner> logger)
{
    public async Task<RetentionPass> RunOnceAsync(CancellationToken cancellationToken)
    {
        var settings = options.Value;
        // Each table on its own: a statement that keeps failing on one must not leave the other growing forever.
        var outbox = await DrainAsync("outbox", store.DeleteOutboxBatchAsync, settings.OutboxRetention, settings.BatchSize, cancellationToken).ConfigureAwait(false);
        var inbox = await DrainAsync("inbox", store.DeleteInboxBatchAsync, settings.InboxRetention, settings.BatchSize, cancellationToken).ConfigureAwait(false);
        if (outbox > 0 || inbox > 0)
            LogDeleted(logger, outbox, inbox);
        return new RetentionPass(outbox, inbox);
    }

    /// <summary>
    /// Deletes batch after batch until one comes back short: nothing eligible was left when it ran. A failure is
    /// logged and ends this table's pass; what was not deleted is still eligible next time.
    /// </summary>
    private async Task<int> DrainAsync(
        string table, Func<TimeSpan, int, CancellationToken, Task<int>> deleteBatch, TimeSpan retention, int batchSize,
        CancellationToken cancellationToken)
    {
        var total = 0;
        try
        {
            int deleted;
            do
            {
                deleted = await deleteBatch(retention, batchSize, cancellationToken).ConfigureAwait(false);
                total += deleted;
            }
            while (deleted >= batchSize);
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            LogTableFailed(logger, table, exception);
        }
        return total;
    }

    [LoggerMessage(EventId = 40, Level = LogLevel.Information, Message = "Waybill retention deleted {Outbox} outbox row(s) and {Inbox} inbox row(s).")]
    private static partial void LogDeleted(ILogger logger, int outbox, int inbox);

    [LoggerMessage(EventId = 42, Level = LogLevel.Error, Message = "Waybill retention could not clean the {Table} table; trying again at the next pass.")]
    private static partial void LogTableFailed(ILogger logger, string table, Exception exception);
}
