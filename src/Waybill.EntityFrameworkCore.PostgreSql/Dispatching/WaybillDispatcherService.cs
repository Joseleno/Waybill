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

    /// <summary>
    /// Marks the loop as running before it starts: since .NET 10 <see cref="ExecuteAsync"/> runs on its own task, so a
    /// health check right after start would otherwise read "not running".
    /// </summary>
    public override Task StartAsync(CancellationToken cancellationToken)
    {
        _status.Started();
        return base.StartAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        LogStarted(logger, dispatcher.Owner);
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
        var isolationVerified = false;
        long? orderingCheckedAt = null; // Environment.TickCount64 of the last ordering check
        while (!stoppingToken.IsCancellationRequested)
        {
            TimeSpan wait;
            try
            {
                // Before the first claim, once the database answers: a wrong isolation level is a configuration error
                // that no retry fixes, so stop (the health check then reports the loop as not running).
                if (!isolationVerified)
                {
                    var isolation = await dispatcher.DefaultIsolationAsync(stoppingToken).ConfigureAwait(false);
                    if (isolation != IsolationLevelCheck.Required)
                    {
                        LogWrongIsolation(logger, isolation);
                        return;
                    }
                    isolationVerified = true;
                }

                // Ordering is agreed at startup and checked again every partition lease (ADR 0007): an instance that does
                // not order next to one that does would claim keyed rows outside any partition.
                if (orderingCheckedAt is null || Environment.TickCount64 - orderingCheckedAt >= options.Value.PartitionLease.TotalMilliseconds)
                {
                    var problem = await dispatcher.CheckOrderingAsync(atStartup: orderingCheckedAt is null, stoppingToken).ConfigureAwait(false);
                    if (problem is not null)
                    {
                        LogOrderingMismatch(logger, problem);
                        return;
                    }
                    orderingCheckedAt = Environment.TickCount64;
                }

                var cycle = await dispatcher.RunOnceAsync(stoppingToken).ConfigureAwait(false);
                databaseFailures = 0;
                _status.CycleCompleted(cycle.Outcome);

                // A full batch that made progress, or one that left rows of its keys behind (ordering by key takes one row
                // per key per batch, so a busy key alone never fills it): go straight to the next one. Breaker open
                // (connection or channel failure): wait it out, nothing is claimed meanwhile. Otherwise: the polling interval.
                if (cycle.Outcome == DispatchOutcome.Progress && (cycle.Claimed >= cycle.BatchSize || cycle.MoreOfKeys))
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

            wait = CapForPartitions(wait, dispatcher.OrdersByKey, options.Value.PartitionLease);
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

    /// <summary>
    /// With ordering, no wait between cycles (polling, breaker, database backoff) outlasts a quarter of the partition
    /// lease: the partitions are renewed at the start of each cycle, and must not expire between two of them (ADR 0007).
    /// </summary>
    internal static TimeSpan CapForPartitions(TimeSpan wait, bool ordered, TimeSpan partitionLease) =>
        ordered && wait > partitionLease / 4 ? partitionLease / 4 : wait;

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
        _status.Stopped(); // also when ExecuteAsync never got to run
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

    [LoggerMessage(EventId = 25, Level = LogLevel.Critical,
        Message = "The Waybill dispatcher stopped: default_transaction_isolation is '{Isolation}', and the claim needs 'read committed'. " +
            "Set it for the role or the database (ALTER ROLE ... SET default_transaction_isolation = 'read committed') and restart.")]
    private static partial void LogWrongIsolation(ILogger logger, string isolation);

    [LoggerMessage(EventId = 26, Level = LogLevel.Critical,
        Message = "The Waybill dispatcher stopped: ordering by key is not agreed between dispatchers: {Problem}.")]
    private static partial void LogOrderingMismatch(ILogger logger, string problem);
}
