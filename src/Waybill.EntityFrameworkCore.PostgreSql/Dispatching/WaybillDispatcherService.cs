using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Waybill.EntityFrameworkCore.Dispatching;

/// <summary>Hosts one dispatcher instance: cycles until the host stops, then hands back what it still holds.</summary>
internal sealed partial class WaybillDispatcherService(
    OutboxDispatcher dispatcher, IOptions<WaybillDispatcherOptions> options, ILogger<WaybillDispatcherService> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        LogStarted(logger, dispatcher.Owner);
        while (!stoppingToken.IsCancellationRequested)
        {
            int claimed;
            try
            {
                claimed = await dispatcher.RunOnceAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                // The database is unreachable or a statement failed: nothing was lost (claimed rows come back with
                // their lease), so wait and try again.
                LogCycleFailed(logger, exception);
                claimed = 0;
            }

            if (claimed >= options.Value.BatchSize)
                continue;

            try
            {
                await Task.Delay(options.Value.PollingInterval, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    /// <summary>Stops claiming, lets the batch in flight finish, then hands back every row this instance still holds.</summary>
    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var released = await dispatcher.ReleaseOwnedAsync(cancellationToken).ConfigureAwait(false);
            LogStopped(logger, dispatcher.Owner, released);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            LogReleaseFailed(logger, exception); // the leases will expire on their own
        }
    }

    [LoggerMessage(EventId = 20, Level = LogLevel.Information, Message = "Waybill dispatcher {Owner} started.")]
    private static partial void LogStarted(ILogger logger, string owner);

    [LoggerMessage(EventId = 21, Level = LogLevel.Information, Message = "Waybill dispatcher {Owner} stopped; {Released} claimed row(s) handed back.")]
    private static partial void LogStopped(ILogger logger, string owner, int released);

    [LoggerMessage(EventId = 22, Level = LogLevel.Error, Message = "A dispatcher cycle failed; retrying after the polling interval.")]
    private static partial void LogCycleFailed(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 23, Level = LogLevel.Warning, Message = "Handing back claimed rows at shutdown failed; their leases will expire on their own.")]
    private static partial void LogReleaseFailed(ILogger logger, Exception exception);
}
