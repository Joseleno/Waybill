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
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
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
}
