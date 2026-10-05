using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Waybill.EntityFrameworkCore;

/// <summary>Reads the connection string the application configured for a <see cref="DbContext"/> (ADR 0005).</summary>
internal static class DbContextConnectionString
{
    public static string? Of<TContext>(IServiceScopeFactory scopes)
        where TContext : DbContext
    {
        using var scope = scopes.CreateScope();
        return scope.ServiceProvider.GetRequiredService<TContext>().Database.GetConnectionString();
    }
}
