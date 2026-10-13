using System.Diagnostics.Metrics;

namespace Waybill.EntityFrameworkCore.Dispatching;

/// <summary>
/// Waybill's instruments on the <c>Waybill</c> meter (ADR 0004). The gauge's callback runs on the exporter's schedule
/// and is synchronous, so it only reads the last sampled value; the database is queried by
/// <see cref="OldestPendingSampler"/>, never from the callback.
/// </summary>
internal sealed class OutboxMetrics : IDisposable
{
    public const string MeterName = "Waybill";

    private readonly Meter _meter;
    private readonly bool _ownsMeter;
    private double _oldestPendingAge;

    public OutboxMetrics(IMeterFactory? factory)
    {
        // A meter from the host's factory is disposed with the container; one created here is disposed by this class.
        _ownsMeter = factory is null;
        _meter = factory?.Create(new MeterOptions(MeterName)) ?? new Meter(MeterName);
        _meter.CreateObservableGauge(
            "waybill.outbox.oldest_pending.age",
            () => Volatile.Read(ref _oldestPendingAge),
            unit: "s",
            description: "Age of the oldest outbox message not yet published (pending or claimed); 0 when there is none.");
        _meter.CreateObservableGauge(
            "waybill.outbox.blocked_keys",
            BlockedKeysMeasurements,
            unit: "{key}",
            description: "Keys stopped by a message in the outbox DLQ while ordering by key is on, until it is requeued or released; not reported while ordering is off.");
    }

    private long _blockedKeys = -1; // -1: ordering off, nothing to report

    private IEnumerable<Measurement<long>> BlockedKeysMeasurements()
    {
        var blocked = Volatile.Read(ref _blockedKeys);
        return blocked < 0 ? [] : [new Measurement<long>(blocked)];
    }

    /// <summary>The last sampled count of blocked keys; null while ordering is off.</summary>
    public long? BlockedKeys => Volatile.Read(ref _blockedKeys) is var blocked and >= 0 ? blocked : null;

    public void RecordBlockedKeys(long? blockedKeys) => Volatile.Write(ref _blockedKeys, blockedKeys ?? -1);

    /// <summary>The last sampled age, in seconds.</summary>
    public double OldestPendingAge => Volatile.Read(ref _oldestPendingAge);

    public void Record(double oldestPendingAgeSeconds) => Volatile.Write(ref _oldestPendingAge, oldestPendingAgeSeconds);

    public void Dispose()
    {
        if (_ownsMeter)
            _meter.Dispose();
    }
}
