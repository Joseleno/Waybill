using System.Diagnostics.Metrics;

namespace Waybill.EntityFrameworkCore;

/// <summary>
/// The inbox's instruments on Waybill's meter. A service that only consumes has no dispatcher, so the inbox registers
/// them itself, once for every context it serves.
/// </summary>
internal sealed class InboxMetrics : IDisposable
{
    private readonly Meter _meter;
    private readonly bool _ownsMeter;
    private readonly Counter<long> _regressions;

    public InboxMetrics(IMeterFactory? factory)
    {
        // A meter from the host's factory is disposed with the container; one created here is disposed by this class.
        _ownsMeter = factory is null;
        _meter = WaybillDiagnostics.CreateMeter(factory);
        _regressions = _meter.CreateCounter<long>(
            "waybill.inbox.regressions",
            unit: "{message}",
            description: "Messages the inbox's sequence check refused as regressions, by handler and reason (lower, reused); each one is also logged with EventId 50.");
    }

    public void RecordRegression(string handler, string reason) =>
        _regressions.Add(1, new KeyValuePair<string, object?>("waybill.inbox.handler", handler), new KeyValuePair<string, object?>("waybill.inbox.regression", reason));

    public void Dispose()
    {
        if (_ownsMeter)
            _meter.Dispose();
    }
}
