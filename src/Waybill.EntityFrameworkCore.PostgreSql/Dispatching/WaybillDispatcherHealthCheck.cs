using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace Waybill.EntityFrameworkCore.Dispatching;

/// <summary>
/// Reports the dispatcher's health (ADR 0004). Rules, in order: the loop not running, or not finishing a cycle for
/// longer than the lease plus the maximum backoff, is Unhealthy; so are repeated database failures. The broker down
/// (breaker open or a connection failure) is Degraded, not Unhealthy: the outbox keeps accepting events.
/// </summary>
internal sealed class WaybillDispatcherHealthCheck(
    DispatcherStatus status, IOptions<WaybillDispatcherOptions> options, OutboxMetrics? metrics, TimeProvider time) : IHealthCheck
{
    internal const int DatabaseFailureThreshold = 3;

    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var current = status.Current;
        var data = metrics is null
            ? new Dictionary<string, object>()
            : new Dictionary<string, object> { ["oldest_pending_age_seconds"] = metrics.OldestPendingAge };
        var stallAfter = options.Value.Lease + WaybillDispatcherService.MaxBackoff;
        var lastSign = current.LastCycleAt ?? current.StartedAt;

        var (health, description) = current switch
        {
            { Running: false } => (HealthStatus.Unhealthy, "The Waybill dispatcher is not running."),
            _ when time.GetUtcNow() - lastSign > stallAfter =>
                (HealthStatus.Unhealthy, $"The Waybill dispatcher has stalled: no cycle finished in the last {stallAfter.TotalSeconds:0} s."),
            { ConsecutiveDatabaseFailures: >= DatabaseFailureThreshold } =>
                (HealthStatus.Unhealthy, $"The Waybill dispatcher cannot reach the database ({current.ConsecutiveDatabaseFailures} cycles failed in a row)."),
            { LastOutcome: DispatchOutcome.ConnectionFailure or DispatchOutcome.BreakerOpen } =>
                (HealthStatus.Degraded, "The broker is unreachable; events are kept in the outbox and published when it is back."),
            _ => (HealthStatus.Healthy, "The Waybill dispatcher is running."),
        };
        return Task.FromResult(new HealthCheckResult(health, description, data: data));
    }
}
