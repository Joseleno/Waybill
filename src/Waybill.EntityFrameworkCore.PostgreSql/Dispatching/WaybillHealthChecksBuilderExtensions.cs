using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace Waybill.EntityFrameworkCore.Dispatching;

/// <summary>Registers the dispatcher's health check.</summary>
public static class WaybillHealthChecksBuilderExtensions
{
    /// <summary>
    /// Adds a health check for the outbox dispatcher registered by <c>services.AddWaybillDispatcher(...)</c> in this
    /// process. Unhealthy when the dispatcher loop is not running or has stalled, or the database failed three cycles
    /// in a row; Degraded when the broker is unreachable (the outbox keeps accepting events); Healthy otherwise. The
    /// entry's data carries <c>oldest_pending_age_seconds</c>.
    /// </summary>
    public static IHealthChecksBuilder AddWaybillDispatcherCheck(
        this IHealthChecksBuilder builder, string name = "waybill-dispatcher", IEnumerable<string>? tags = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return builder.Add(new HealthCheckRegistration(
            name,
            sp => new WaybillDispatcherHealthCheck(
                sp.GetService<DispatcherStatus>() ?? throw new InvalidOperationException(
                    "The Waybill dispatcher health check needs services.AddWaybillDispatcher(...) in the same container."),
                sp.GetRequiredService<IOptions<WaybillDispatcherOptions>>(),
                sp.GetService<OutboxMetrics>(),
                TimeProvider.System),
            failureStatus: null,
            tags));
    }
}
