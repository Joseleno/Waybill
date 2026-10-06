using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Waybill.EntityFrameworkCore;
using Waybill.Testing;

namespace Microsoft.Extensions.DependencyInjection;

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

    /// <summary>
    /// Registers <see cref="FakeInbox{TContext}"/> as the scoped <see cref="IInbox{TContext}"/>, replacing the real one.
    /// One <see cref="FakeInboxMemory"/> (singleton) remembers processed messages across scopes, like the real inbox
    /// table; resolve it to assert. The handler receives the scope's <typeparamref name="TContext"/>.
    /// </summary>
    public static IServiceCollection AddFakeWaybillInbox<TContext>(this IServiceCollection services)
        where TContext : DbContext
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton<FakeInboxMemory>();
        services.Replace(ServiceDescriptor.Scoped<IInbox<TContext>>(sp =>
            new FakeInbox<TContext>(sp.GetRequiredService<TContext>(), sp.GetRequiredService<FakeInboxMemory>())));
        return services;
    }
}
