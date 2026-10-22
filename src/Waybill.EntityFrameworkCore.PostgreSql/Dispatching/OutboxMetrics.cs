using System.Diagnostics.Metrics;

namespace Waybill.EntityFrameworkCore.Dispatching;

/// <summary>
/// Waybill's instruments on the <c>Waybill</c> meter (ADR 0004). The gauge's callback runs on the exporter's schedule
/// and is synchronous, so it only reads the last sampled value; the database is queried by
/// <see cref="OldestPendingSampler"/>, never from the callback.
/// </summary>
internal sealed class OutboxMetrics : IDisposable
{
    public const string MeterName = WaybillDiagnostics.MeterName;

    private readonly Meter _meter;
    private readonly bool _ownsMeter;
    private readonly Counter<long> _headsRepaired;
    private double _oldestPendingAge;
    private long _headsRepairedTotal;

    public OutboxMetrics(IMeterFactory? factory)
    {
        // A meter from the host's factory is disposed with the container; one created here is disposed by this class.
        _ownsMeter = factory is null;
        _meter = WaybillDiagnostics.CreateMeter(factory);
        _meter.CreateObservableGauge(
            "waybill.outbox.oldest_pending.age",
            () => Volatile.Read(ref _oldestPendingAge),
            unit: "s",
            description: "Age of the oldest outbox message a claim could take (pending or claimed, and with ordering by key the first unfinished one of its key); 0 when there is none.");
        _meter.CreateObservableGauge(
            "waybill.outbox.blocked_keys",
            BlockedKeysMeasurements,
            unit: "{key}",
            description: "Keys stopped by a message in the outbox DLQ while ordering by key is on, until it is requeued or released; not reported while ordering is off.");
        _meter.CreateObservableGauge(
            "waybill.outbox.dlq",
            DlqMeasurements,
            unit: "{message}",
            description: "Messages in the outbox DLQ, with ordering on or off, until they are requeued or released. Every dispatcher instance reports the same total: aggregate by max, not sum.");
        _headsRepaired = _meter.CreateCounter<long>(
            "waybill.outbox.heads_repaired",
            unit: "{message}",
            description: "Messages a dispatcher found first in their key yet not marked claimable, and marked: zero unless something changed the outbox outside Waybill (a disabled trigger, a replica session, a restore).");
    }

    /// <summary>How many key heads this instance repaired since it started.</summary>
    public long HeadsRepaired => Interlocked.Read(ref _headsRepairedTotal);

    public void RecordHeadsRepaired(int count)
    {
        if (count == 0)
            return;
        Interlocked.Add(ref _headsRepairedTotal, count);
        _headsRepaired.Add(count);
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

    private long _dlq = -1; // -1: not sampled yet; a 0 would read as an empty DLQ

    private IEnumerable<Measurement<long>> DlqMeasurements()
    {
        var dlq = Volatile.Read(ref _dlq);
        return dlq < 0 ? [] : [new Measurement<long>(dlq)];
    }

    /// <summary>The last sampled count of messages in the DLQ; null before the first sample.</summary>
    public long? Dlq => Volatile.Read(ref _dlq) is var dlq and >= 0 ? dlq : null;

    public void RecordDlq(long dlq) => Volatile.Write(ref _dlq, dlq);

    /// <summary>The last sampled age, in seconds.</summary>
    public double OldestPendingAge => Volatile.Read(ref _oldestPendingAge);

    public void Record(double oldestPendingAgeSeconds) => Volatile.Write(ref _oldestPendingAge, oldestPendingAgeSeconds);

    public void Dispose()
    {
        if (_ownsMeter)
            _meter.Dispose();
    }
}
