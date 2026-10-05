using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Npgsql;
using Waybill.EntityFrameworkCore.Retention;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>Registers retention cleanup.</summary>
public static class WaybillRetentionServiceCollectionExtensions
{
    /// <summary>
    /// Runs retention cleanup as a hosted service in this process: published outbox rows and processed inbox rows past
    /// their retention are deleted in small batches. Any number of instances may run it at once. Options are validated
    /// at startup.
    /// </summary>
    public static IServiceCollection AddWaybillRetention(this IServiceCollection services, Action<WaybillRetentionOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        services.AddOptions<WaybillRetentionOptions>()
            .Configure(configure)
            .Validate(o => !string.IsNullOrWhiteSpace(o.ConnectionString), "WaybillRetentionOptions.ConnectionString is required.")
            .Validate(o => o.OutboxRetention > TimeSpan.Zero, "WaybillRetentionOptions.OutboxRetention must be positive.")
            .Validate(o => o.InboxRetention > TimeSpan.Zero, "WaybillRetentionOptions.InboxRetention must be positive.")
            .Validate(o => o.Interval > TimeSpan.Zero && o.Interval <= TimeSpan.FromDays(1), "WaybillRetentionOptions.Interval must be positive and at most one day.")
            .Validate(o => o.BatchSize > 0, "WaybillRetentionOptions.BatchSize must be positive.")
            .ValidateOnStart();

        services.TryAddSingleton(sp => new RetentionDataSource(
            NpgsqlDataSource.Create(sp.GetRequiredService<IOptions<WaybillRetentionOptions>>().Value.ConnectionString!)));
        services.TryAddSingleton(sp => new RetentionStore(sp.GetRequiredService<RetentionDataSource>().Value));
        services.TryAddSingleton<RetentionCleaner>();
        services.AddHostedService<WaybillRetentionService>();
        return services;
    }
}
