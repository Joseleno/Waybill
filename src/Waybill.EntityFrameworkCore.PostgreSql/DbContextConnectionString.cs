using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Waybill.EntityFrameworkCore;

/// <summary>Reads the connection string the application configured for a <see cref="DbContext"/> (ADR 0005).</summary>
/// <remarks>
/// With <c>UseNpgsql(NpgsqlDataSource)</c>, Npgsql leaves the password out of that string; such applications set
/// <c>ConnectionString</c> explicitly.
/// </remarks>
internal static class DbContextConnectionString
{
    public static string? Of<TContext>(IServiceScopeFactory scopes)
        where TContext : DbContext
    {
        using var scope = scopes.CreateScope();
        var context = scope.ServiceProvider.GetService<TContext>() ?? throw new InvalidOperationException(
            $"Waybill reads the connection string from {typeof(TContext).Name}, but it is not registered as a service. " +
            $"Register it with services.AddDbContext<{typeof(TContext).Name}>(...), or set ConnectionString in the options.");
        return context.Database.GetConnectionString();
    }
}
