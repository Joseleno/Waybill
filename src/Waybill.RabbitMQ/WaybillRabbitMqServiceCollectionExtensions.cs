using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Waybill.RabbitMQ;

/// <summary>Registers the RabbitMQ transport.</summary>
public static class WaybillRabbitMqServiceCollectionExtensions
{
    /// <summary>
    /// Registers RabbitMQ as the <see cref="ITransport"/> used by the outbox dispatcher. Options are validated at
    /// startup; the connection opens on the first batch.
    /// </summary>
    public static IServiceCollection AddWaybillRabbitMQ(this IServiceCollection services, Action<WaybillRabbitMqOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        services.AddOptions<WaybillRabbitMqOptions>()
            .Configure(configure)
            .Validate(o => o.Uri is not null, "WaybillRabbitMqOptions.Uri is required.")
            .Validate(o => o.Exchange is not null, "WaybillRabbitMqOptions.Exchange is required (use \"\" for the default exchange).")
            .ValidateOnStart();
        services.Replace(ServiceDescriptor.Singleton<ITransport, RabbitMqTransport>());
        return services;
    }
}
