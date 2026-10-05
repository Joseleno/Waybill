using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Waybill.EntityFrameworkCore.Retention;

/// <summary>Runs a cleanup pass every <see cref="WaybillRetentionOptions.Interval"/> until the host stops.</summary>
internal sealed partial class WaybillRetentionService(
    RetentionCleaner cleaner, IOptions<WaybillRetentionOptions> options, ILogger<WaybillRetentionService> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var isolationVerified = false;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // Before the first pass, once the database answers: a wrong isolation level is a configuration error
                // that no retry fixes (IsolationLevelCheck), so stop instead of failing every pass.
                if (!isolationVerified)
                {
                    var isolation = await cleaner.DefaultIsolationAsync(stoppingToken).ConfigureAwait(false);
                    if (isolation != IsolationLevelCheck.Required)
                    {
                        LogWrongIsolation(logger, isolation);
                        return;
                    }
                    isolationVerified = true;
                }

                await cleaner.RunOnceAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                // Nothing is lost: what was not deleted is still eligible for the next pass.
                LogPassFailed(logger, exception);
            }

            try
            {
                await Task.Delay(options.Value.Interval, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    [LoggerMessage(EventId = 41, Level = LogLevel.Error, Message = "A Waybill retention pass failed; trying again at the next interval.")]
    private static partial void LogPassFailed(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 43, Level = LogLevel.Critical,
        Message = "Waybill retention stopped: default_transaction_isolation is '{Isolation}', and the cleanup needs 'read committed'. " +
            "Set it for the role or the database (ALTER ROLE ... SET default_transaction_isolation = 'read committed') and restart.")]
    private static partial void LogWrongIsolation(ILogger logger, string isolation);
}
