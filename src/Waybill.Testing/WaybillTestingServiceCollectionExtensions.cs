using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Waybill.EntityFrameworkCore;

namespace Waybill.Testing;

/// <summary>Swaps the real outbox for <see cref="FakeOutbox{TContext}"/> in a test container.</summary>
public static class WaybillTestingServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="FakeOutbox{TContext}"/> as the scoped <see cref="IOutbox{TContext}"/>, replacing the real
    /// one. Resolve it as <see cref="FakeOutbox{TContext}"/> to assert. Requires <c>services.AddWaybill(...)</c>.
    /// </summary>
    public static IServiceCollection AddFakeWaybillOutbox<TContext>(this IServiceCollection services)
        where TContext : DbContext
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddScoped<FakeOutbox<TContext>>();
        services.Replace(ServiceDescriptor.Scoped<IOutbox<TContext>>(sp => sp.GetRequiredService<FakeOutbox<TContext>>()));
        return services;
    }
}
