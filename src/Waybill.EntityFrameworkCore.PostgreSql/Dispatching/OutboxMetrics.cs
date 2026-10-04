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
    }

    public void Record(double oldestPendingAgeSeconds) => Volatile.Write(ref _oldestPendingAge, oldestPendingAgeSeconds);

    public void Dispose()
    {
        if (_ownsMeter)
            _meter.Dispose();
    }
}
