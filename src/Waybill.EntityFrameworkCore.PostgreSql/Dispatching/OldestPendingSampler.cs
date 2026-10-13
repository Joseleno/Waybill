namespace Waybill.EntityFrameworkCore.Dispatching;

/// <summary>Reads the age of the oldest message still to be published, and the keys a DLQ row stops, and hands them to the gauges.</summary>
internal sealed class OldestPendingSampler(OutboxStore store, OutboxMetrics metrics)
{
    /// <summary>One sample. A failure leaves the gauges at their last values and surfaces to the caller.</summary>
    public async Task SampleAsync(CancellationToken cancellationToken)
    {
        metrics.Record(await store.OldestPendingAgeAsync(cancellationToken).ConfigureAwait(false));
        metrics.RecordBlockedKeys(await store.BlockedKeysAsync(cancellationToken).ConfigureAwait(false));
    }
}
