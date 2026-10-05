using Microsoft.EntityFrameworkCore;
using System.Diagnostics.Metrics;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Npgsql;
using Waybill.EntityFrameworkCore;
using Waybill.EntityFrameworkCore.Dispatching;
using Waybill;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>Registers the outbox dispatcher.</summary>
public static class WaybillDispatcherServiceCollectionExtensions
{
    /// <summary>
    /// Runs the outbox dispatcher for the database of <typeparamref name="TContext"/>: the connection string is read from
    /// the registered context at startup, unless <paramref name="configure"/> sets one. Requires
    /// <c>services.AddWaybill(...)</c> and an <see cref="ITransport"/>. Options are validated at startup.
    /// </summary>
    public static IServiceCollection AddWaybillDispatcher<TContext>(this IServiceCollection services, Action<WaybillDispatcherOptions>? configure = null)
        where TContext : DbContext
    {
        services.AddWaybillDispatcher(configure ?? (_ => { }));
        services.AddOptions<WaybillDispatcherOptions>().PostConfigure<IServiceScopeFactory>((options, scopes) =>
        {
            if (string.IsNullOrWhiteSpace(options.ConnectionString))
                options.ConnectionString = DbContextConnectionString.Of<TContext>(scopes);
        });
        return services;
    }

    /// <summary>
    /// Runs the outbox dispatcher as a hosted service in this process. Requires <c>services.AddWaybill(...)</c> and an
    /// <see cref="ITransport"/> (for example <c>services.AddWaybillRabbitMQ(...)</c>). Options are validated at startup.
    /// </summary>
    public static IServiceCollection AddWaybillDispatcher(this IServiceCollection services, Action<WaybillDispatcherOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        services.AddOptions<WaybillDispatcherOptions>()
            .Configure(configure)
            .Validate(o => !string.IsNullOrWhiteSpace(o.ConnectionString), "WaybillDispatcherOptions.ConnectionString is required.")
            .Validate(o => o.BatchSize > 0, "WaybillDispatcherOptions.BatchSize must be positive.")
            .Validate(o => o.PollingInterval > TimeSpan.Zero && o.PollingInterval <= TimeSpan.FromDays(1), "WaybillDispatcherOptions.PollingInterval must be positive and at most one day.")
            .Validate(o => o.PublishTimeout > TimeSpan.Zero, "WaybillDispatcherOptions.PublishTimeout must be positive.")
            .Validate(o => o.LeaseMargin > TimeSpan.Zero, "WaybillDispatcherOptions.LeaseMargin must be positive.")
            .Validate(o => o.MaxReturns > 0, "WaybillDispatcherOptions.MaxReturns must be positive.")
            .Validate(o => o.MetricsInterval > TimeSpan.Zero && o.MetricsInterval <= TimeSpan.FromDays(1), "WaybillDispatcherOptions.MetricsInterval must be positive and at most one day.")
            .ValidateOnStart();

        services.TryAddSingleton(sp => new DispatcherDataSource(
            NpgsqlDataSource.Create(sp.GetRequiredService<IOptions<WaybillDispatcherOptions>>().Value.ConnectionString!)));
        services.TryAddSingleton(sp => new OutboxStore(sp.GetRequiredService<DispatcherDataSource>().Value));
        services.TryAddSingleton<OutboxDispatcher>();
        services.TryAddSingleton(_ => new DispatcherStatus(TimeProvider.System));
        services.AddHostedService<WaybillDispatcherService>();
        services.TryAddSingleton(sp => new OutboxMetrics(sp.GetService<IMeterFactory>()));
        services.TryAddSingleton<OldestPendingSampler>();
        services.AddHostedService<WaybillMetricsService>();
        return services;
    }
}
