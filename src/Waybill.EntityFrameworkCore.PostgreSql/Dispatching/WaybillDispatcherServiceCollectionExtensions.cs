using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Waybill.EntityFrameworkCore.Dispatching;

/// <summary>Registers the outbox dispatcher.</summary>
public static class WaybillDispatcherServiceCollectionExtensions
{
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
            .Validate(o => o.PollingInterval > TimeSpan.Zero, "WaybillDispatcherOptions.PollingInterval must be positive.")
            .Validate(o => o.PublishTimeout > TimeSpan.Zero, "WaybillDispatcherOptions.PublishTimeout must be positive.")
            .Validate(o => o.LeaseMargin > TimeSpan.Zero, "WaybillDispatcherOptions.LeaseMargin must be positive.")
            .Validate(o => o.MaxReturns > 0, "WaybillDispatcherOptions.MaxReturns must be positive.")
            .ValidateOnStart();

        services.TryAddSingleton(sp => new DispatcherDataSource(
            NpgsqlDataSource.Create(sp.GetRequiredService<IOptions<WaybillDispatcherOptions>>().Value.ConnectionString!)));
        services.TryAddSingleton(sp => new OutboxStore(sp.GetRequiredService<DispatcherDataSource>().Value));
        services.TryAddSingleton<OutboxDispatcher>();
        services.AddHostedService<WaybillDispatcherService>();
        return services;
    }
}

/// <summary>The dispatcher's own data source, owned (and disposed) by the container.</summary>
internal sealed class DispatcherDataSource(NpgsqlDataSource value) : IAsyncDisposable, IDisposable
{
    public NpgsqlDataSource Value { get; } = value;

    public ValueTask DisposeAsync() => Value.DisposeAsync();

    public void Dispose() => Value.Dispose();
}
