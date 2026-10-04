using System.Diagnostics.Metrics;

namespace Waybill.Tests.Integration.Observabilidade;

/// <summary>
/// Reads Waybill's oldest-pending-age gauge the way an exporter would: one collection, through a listener. Only the
/// meter created by <paramref name="factory"/> is read, so tests running in parallel do not see each other's gauges.
/// </summary>
public static class GaugeReader
{
    public const string MeterName = "Waybill";
    public const string OldestPendingAge = "waybill.outbox.oldest_pending.age";

    public static (double? Value, string? Unit) Read(IMeterFactory factory)
    {
        double? value = null;
        string? unit = null;
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == MeterName && instrument.Name == OldestPendingAge && ReferenceEquals(instrument.Meter.Scope, factory))
            {
                unit = instrument.Unit;
                l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<double>((_, measurement, _, _) => value = measurement);
        listener.Start();
        listener.RecordObservableInstruments();
        return (value, unit);
    }
}
