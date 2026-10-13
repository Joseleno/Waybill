using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Waybill.EntityFrameworkCore;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>Wires Waybill's outbox into an application's <see cref="DbContext"/>.</summary>
public static class WaybillEntityFrameworkCoreExtensions
{
    /// <summary>
    /// Registers <see cref="IOutbox{TContext}"/> as a scoped service bound to the scope's <typeparamref name="TContext"/>,
    /// which must map the outbox with <c>modelBuilder.MapWaybillOutbox()</c>, and adds to <typeparamref name="TContext"/>
    /// the interceptor that orders the key locks of a <c>SaveChanges</c> when ordering by key is on. The context must be
    /// registered with <c>AddDbContext</c>, <c>AddDbContextPool</c> or a context factory. Requires <c>services.AddWaybill(...)</c>.
    /// </summary>
    public static IServiceCollection AddWaybillOutbox<TContext>(this IServiceCollection services)
        where TContext : DbContext
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddScoped<IOutbox<TContext>, DbContextOutbox<TContext>>();
        services.ConfigureDbContext<TContext>(options => options.AddInterceptors(OutboxKeyLockInterceptor.Instance));
        return services;
    }

    /// <summary>
    /// Registers <see cref="IInbox{TContext}"/> as a scoped service bound to the scope's <typeparamref name="TContext"/>,
    /// and guards every <typeparamref name="TContext"/> against saving outside the inbox transaction while a handler runs.
    /// The tables come from <see cref="WaybillSchema.MigrateAsync"/>.
    /// </summary>
    public static IServiceCollection AddWaybillInbox<TContext>(this IServiceCollection services)
        where TContext : DbContext
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddScoped<IInbox<TContext>, DbContextInbox<TContext>>();
        services.ConfigureDbContext<TContext>(options => options.AddInterceptors(InboxGuardInterceptor.Instance));
        return services;
    }
}
