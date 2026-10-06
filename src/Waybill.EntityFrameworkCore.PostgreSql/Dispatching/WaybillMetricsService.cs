using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Waybill.EntityFrameworkCore.Dispatching;

/// <summary>Samples the oldest pending age every <see cref="WaybillDispatcherOptions.MetricsInterval"/> until the host stops.</summary>
internal sealed partial class WaybillMetricsService(
    OldestPendingSampler sampler, IOptions<WaybillDispatcherOptions> options, ILogger<WaybillMetricsService> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await sampler.SampleAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                // The gauge keeps its last value: reporting 0 would read as an empty outbox.
                LogSampleFailed(logger, exception);
            }

            try
            {
                await Task.Delay(options.Value.MetricsInterval, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    [LoggerMessage(EventId = 24, Level = LogLevel.Error, Message = "Sampling the age of the oldest pending outbox message failed; the gauge keeps its last value.")]
    private static partial void LogSampleFailed(ILogger logger, Exception exception);
}
