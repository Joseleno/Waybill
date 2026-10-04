using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Waybill.EntityFrameworkCore;

/// <summary>Wires Waybill's outbox into an application's <see cref="DbContext"/>.</summary>
public static class WaybillEntityFrameworkCoreExtensions
{
    /// <summary>
    /// Registers <see cref="IOutbox{TContext}"/> as a scoped service bound to the scope's <typeparamref name="TContext"/>,
    /// which must map the outbox with <c>modelBuilder.AddWaybillOutbox()</c>. Requires <c>services.AddWaybill(...)</c>.
    /// </summary>
    public static IServiceCollection AddWaybillOutbox<TContext>(this IServiceCollection services)
        where TContext : DbContext
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddScoped<IOutbox<TContext>, DbContextOutbox<TContext>>();
        return services;
    }
}
