namespace Waybill.EntityFrameworkCore.Dispatching;

/// <summary>
/// Reads the age of the oldest message still to be published, the keys a DLQ row stops and the messages in the DLQ,
/// and hands them to the gauges.
/// </summary>
internal sealed class OldestPendingSampler(OutboxStore store, OutboxMetrics metrics)
{
    /// <summary>One sample. A failure leaves the gauges at their last values and surfaces to the caller.</summary>
    public async Task SampleAsync(CancellationToken cancellationToken)
    {
        var (oldestPendingAge, blockedKeys, dlq) = await store.SampleAsync(cancellationToken).ConfigureAwait(false);
        metrics.Record(oldestPendingAge);
        metrics.RecordBlockedKeys(blockedKeys);
        metrics.RecordDlq(dlq);
    }
}
