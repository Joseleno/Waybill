using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Waybill.EntityFrameworkCore.Dispatching;

/// <summary>Hosts one dispatcher instance: cycles until the host stops, then hands back what it still holds.</summary>
internal sealed partial class WaybillDispatcherService(
    OutboxDispatcher dispatcher, IOptions<WaybillDispatcherOptions> options, ILogger<WaybillDispatcherService> logger)
    : BackgroundService
{
    /// <summary>Upper bound of the wait between cycles while the transport or the database keeps failing.</summary>
    internal static readonly TimeSpan MaxBackoff = TimeSpan.FromSeconds(30);

    private static readonly TimeSpan ReleaseTimeout = TimeSpan.FromSeconds(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        LogStarted(logger, dispatcher.Owner);
        var failures = 0;
        while (!stoppingToken.IsCancellationRequested)
        {
            DispatchCycle cycle;
            try
            {
                cycle = await dispatcher.RunOnceAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                // The database is unreachable or a statement failed: nothing was lost (claimed rows come back with
                // their lease or at shutdown), so back off and try again.
                LogCycleFailed(logger, exception);
                cycle = new DispatchCycle(0, TransportFailed: true);
            }

            // A full batch that made progress: go straight to the next one. A failing transport or database: back off
            // (doubling up to MaxBackoff), so a broker outage with a large backlog does not turn into a claim storm.
            failures = cycle.TransportFailed ? failures + 1 : 0;
            if (failures == 0 && cycle.Claimed >= options.Value.BatchSize)
                continue;

            try
            {
                await Task.Delay(Wait(options.Value.PollingInterval, failures), stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    internal static TimeSpan Wait(TimeSpan pollingInterval, int consecutiveFailures)
    {
        if (consecutiveFailures == 0)
            return pollingInterval;
        var factor = Math.Pow(2, Math.Min(consecutiveFailures - 1, 16));
        return TimeSpan.FromTicks((long)Math.Min(pollingInterval.Ticks * factor, Math.Max(MaxBackoff.Ticks, pollingInterval.Ticks)));
    }

    /// <summary>
    /// Stops claiming, lets the batch in flight finish, then hands back every row this instance still holds — with its
    /// own short deadline, so an expired host shutdown token does not leave the rows waiting for their lease.
    /// </summary>
    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken).ConfigureAwait(false);
        using var release = new CancellationTokenSource(ReleaseTimeout);
        try
        {
            var released = await dispatcher.ReleaseOwnedAsync(release.Token).ConfigureAwait(false);
            LogStopped(logger, dispatcher.Owner, released);
        }
        catch (Exception exception)
        {
            LogReleaseFailed(logger, exception); // the leases will expire on their own
        }
    }

    [LoggerMessage(EventId = 20, Level = LogLevel.Information, Message = "Waybill dispatcher {Owner} started.")]
    private static partial void LogStarted(ILogger logger, string owner);

    [LoggerMessage(EventId = 21, Level = LogLevel.Information, Message = "Waybill dispatcher {Owner} stopped; {Released} claimed row(s) handed back.")]
    private static partial void LogStopped(ILogger logger, string owner, int released);

    [LoggerMessage(EventId = 22, Level = LogLevel.Error, Message = "A dispatcher cycle failed; backing off before the next one.")]
    private static partial void LogCycleFailed(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 23, Level = LogLevel.Warning, Message = "Handing back claimed rows at shutdown failed; their leases will expire on their own.")]
    private static partial void LogReleaseFailed(ILogger logger, Exception exception);
}
