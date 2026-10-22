using System.Diagnostics.Metrics;

namespace Waybill.Tests.Integration.Observabilidade;

/// <summary>
/// Reads one of Waybill's gauges the way an exporter would: one collection, through a listener. Only the meter created
/// by <c>factory</c> is read, so tests running in parallel do not see each other's gauges.
/// </summary>
public static class GaugeReader
{
    public const string MeterName = "Waybill";
    public const string OldestPendingAge = "waybill.outbox.oldest_pending.age";
    public const string BlockedKeys = "waybill.outbox.blocked_keys";
    public const string Dlq = "waybill.outbox.dlq";

    public static (double? Value, string? Unit) Read(IMeterFactory factory) => Read(factory, OldestPendingAge);

    /// <returns>The measurement, or null when the gauge reported none; and the gauge's unit.</returns>
    public static (double? Value, string? Unit) Read(IMeterFactory factory, string gauge)
    {
        double? value = null;
        string? unit = null;
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == MeterName && instrument.Name == gauge && ReferenceEquals(instrument.Meter.Scope, factory))
            {
                unit = instrument.Unit;
                l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<double>((_, measurement, _, _) => value = measurement);
        listener.SetMeasurementEventCallback<long>((_, measurement, _, _) => value = measurement);
        listener.Start();
        listener.RecordObservableInstruments();
        return (value, unit);
    }
}
