using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Waybill.EntityFrameworkCore.Dispatching;

/// <summary>Hosts one dispatcher instance: cycles until the host stops, then hands back what it still holds.</summary>
internal sealed partial class WaybillDispatcherService(
    OutboxDispatcher dispatcher, IOptions<WaybillDispatcherOptions> options, ILogger<WaybillDispatcherService> logger,
    DispatcherStatus? status = null)
    : BackgroundService
{
    private readonly DispatcherStatus _status = status ?? new DispatcherStatus(TimeProvider.System);

    /// <summary>Upper bound of the wait between cycles while the transport or the database keeps failing.</summary>
    internal static readonly TimeSpan MaxBackoff = TimeSpan.FromSeconds(30);

    private static readonly TimeSpan ReleaseTimeout = TimeSpan.FromSeconds(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        LogStarted(logger, dispatcher.Owner);
        _status.Started();
        try
        {
            await RunAsync(stoppingToken).ConfigureAwait(false);
        }
        finally
        {
            _status.Stopped();
        }
    }

    private async Task RunAsync(CancellationToken stoppingToken)
    {
        var databaseFailures = 0;
        while (!stoppingToken.IsCancellationRequested)
        {
            TimeSpan wait;
            try
            {
                var cycle = await dispatcher.RunOnceAsync(stoppingToken).ConfigureAwait(false);
                databaseFailures = 0;
                _status.CycleCompleted(cycle.Outcome);

                // A full batch that made progress: go straight to the next one. Breaker open (connection or channel
                // failure): wait it out, nothing is claimed meanwhile. Otherwise: the polling interval.
                if (cycle.Outcome == DispatchOutcome.Progress && cycle.Claimed >= cycle.BatchSize)
                    continue;
                wait = cycle.Outcome is DispatchOutcome.ConnectionFailure or DispatchOutcome.BreakerOpen
                    ? Max(dispatcher.BreakerRemaining, TimeSpan.FromMilliseconds(1))
                    : options.Value.PollingInterval;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                // The database is unreachable or a statement failed: nothing was lost (claimed rows come back with
                // their lease or at shutdown), so back off (doubling up to MaxBackoff) and try again.
                LogCycleFailed(logger, exception);
                _status.CycleFailed();
                wait = Wait(options.Value.PollingInterval, ++databaseFailures);
            }

            try
            {
                await Task.Delay(wait, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    internal static TimeSpan Wait(TimeSpan pollingInterval, int consecutiveFailures) =>
        CircuitBreaker.Delay(pollingInterval, MaxBackoff, consecutiveFailures);

    private static TimeSpan Max(TimeSpan a, TimeSpan b) => a > b ? a : b;

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
