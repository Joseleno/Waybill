namespace Waybill.EntityFrameworkCore.Dispatching;

/// <summary>Reads the age of the oldest message still to be published and hands it to the gauge.</summary>
internal sealed class OldestPendingSampler(OutboxStore store, OutboxMetrics metrics)
{
    /// <summary>One sample. A failure leaves the gauge at its last value and surfaces to the caller.</summary>
    public async Task SampleAsync(CancellationToken cancellationToken) =>
        metrics.Record(await store.OldestPendingAgeAsync(cancellationToken).ConfigureAwait(false));
}
