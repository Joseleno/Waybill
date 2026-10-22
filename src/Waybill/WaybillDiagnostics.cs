using System.Diagnostics.Metrics;

namespace Waybill;

/// <summary>The names an application listens to, to collect what Waybill reports.</summary>
public static class WaybillDiagnostics
{
    /// <summary>The meter of Waybill's metrics, for <c>AddMeter</c>.</summary>
    public const string MeterName = "Waybill";

    /// <summary>
    /// The activity source of Waybill's spans, for <c>AddSource</c>: <c>create</c> at Enqueue, <c>send</c> for each message
    /// the dispatcher publishes, <c>process</c> in the inbox. With RabbitMQ, listen to the client's publisher source too
    /// (<c>RabbitMQActivitySource.PublisherSourceName</c>).
    /// </summary>
    public const string ActivitySourceName = "Waybill";

    /// <summary>
    /// Waybill's meter, from the host's factory when there is one. Every package builds it here, with the same options,
    /// so a factory hands the same meter to all of them. A meter from the factory is disposed with the container; one
    /// created here (no factory) is the caller's to dispose.
    /// </summary>
    internal static Meter CreateMeter(IMeterFactory? factory) =>
        factory?.Create(new MeterOptions(MeterName)) ?? new Meter(MeterName);
}
