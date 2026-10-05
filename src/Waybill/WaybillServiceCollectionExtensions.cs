using Waybill;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>Registers Waybill's core configuration.</summary>
public static class WaybillServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="WaybillOptions"/>. <see cref="WaybillOptions.MaxPayloadBytes"/> is required and is
    /// validated when the application starts.
    /// </summary>
    public static IServiceCollection AddWaybill(this IServiceCollection services, Action<WaybillOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        services.AddOptions<WaybillOptions>()
            .Configure(configure)
            .Validate(o => o.MaxPayloadBytes > 0, "WaybillOptions.MaxPayloadBytes must be configured with a positive value.")
            .ValidateOnStart();
        return services;
    }
}
