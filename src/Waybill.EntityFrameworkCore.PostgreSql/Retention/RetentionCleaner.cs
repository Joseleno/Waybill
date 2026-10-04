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
        var outbox = await DrainAsync(store.DeleteOutboxBatchAsync, settings.OutboxRetention, settings.BatchSize, cancellationToken).ConfigureAwait(false);
        if (outbox > 0)
            LogDeleted(logger, outbox, 0);
        return new RetentionPass(outbox, 0);
    }

    /// <summary>Deletes batch after batch until one comes back short: nothing eligible was left when it ran.</summary>
    private static async Task<int> DrainAsync(
        Func<TimeSpan, int, CancellationToken, Task<int>> deleteBatch, TimeSpan retention, int batchSize, CancellationToken cancellationToken)
    {
        var total = 0;
        int deleted;
        do
        {
            deleted = await deleteBatch(retention, batchSize, cancellationToken).ConfigureAwait(false);
            total += deleted;
        }
        while (deleted >= batchSize);
        return total;
    }

    [LoggerMessage(EventId = 40, Level = LogLevel.Information, Message = "Waybill retention deleted {Outbox} outbox row(s) and {Inbox} inbox row(s).")]
    private static partial void LogDeleted(ILogger logger, int outbox, int inbox);
}
