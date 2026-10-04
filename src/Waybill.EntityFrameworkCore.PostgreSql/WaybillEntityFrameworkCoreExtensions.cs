using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Waybill.EntityFrameworkCore;

/// <summary>Wires Waybill's outbox into an application's <see cref="DbContext"/>.</summary>
public static class WaybillEntityFrameworkCoreExtensions
{
    /// <summary>
    /// Adds the interceptor that inserts enqueued messages in the same <c>SaveChanges</c> and transaction as the
    /// application's data. Pair it with <c>modelBuilder.AddWaybillOutbox()</c> in <c>OnModelCreating</c>.
    /// </summary>
    public static DbContextOptionsBuilder UseWaybill(this DbContextOptionsBuilder optionsBuilder)
    {
        ArgumentNullException.ThrowIfNull(optionsBuilder);
        return optionsBuilder.AddInterceptors(WaybillSaveChangesInterceptor.Instance);
    }

    /// <summary>
    /// Registers <see cref="IOutbox{TContext}"/> as a scoped service bound to the scope's <typeparamref name="TContext"/>.
    /// Requires <c>services.AddWaybill(...)</c>.
    /// </summary>
    public static IServiceCollection AddWaybillOutbox<TContext>(this IServiceCollection services)
        where TContext : DbContext
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddScoped<IOutbox<TContext>, DbContextOutbox<TContext>>();
        return services;
    }
}
