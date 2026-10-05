using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Waybill.EntityFrameworkCore.Dispatching;
using Waybill.EntityFrameworkCore.Retention;

namespace Waybill.Tests.Unit;

// ADR 0005: the outbox lives in the DbContext's database, so the dispatcher and retention can take the connection
// string from it instead of having it repeated; an explicit one still wins, and a context without one fails at startup.
public sealed class RegistroDoContextoTests
{
    private const string FromContext = "Host=db;Database=orders;Username=app";
    private const string Explicit = "Host=other;Database=orders;Username=waybill";

    [Fact]
    public void Registro_DispatcherDoContexto_UsaAConnectionStringDoDbContext()
    {
        using var services = Build(s => s.AddWaybillDispatcher<OrdersDbContext>().AddWaybillRetention<OrdersDbContext>());

        Assert.Equal(FromContext, services.GetRequiredService<IOptions<WaybillDispatcherOptions>>().Value.ConnectionString);
        Assert.Equal(FromContext, services.GetRequiredService<IOptions<WaybillRetentionOptions>>().Value.ConnectionString);
    }

    [Fact]
    public void Registro_ConnectionStringExplicita_TemPrecedencia()
    {
        using var services = Build(s => s
            .AddWaybillDispatcher<OrdersDbContext>(o => o.ConnectionString = Explicit)
            .AddWaybillRetention<OrdersDbContext>(o => o.ConnectionString = Explicit));

        Assert.Equal(Explicit, services.GetRequiredService<IOptions<WaybillDispatcherOptions>>().Value.ConnectionString);
        Assert.Equal(Explicit, services.GetRequiredService<IOptions<WaybillRetentionOptions>>().Value.ConnectionString);
    }

    [Fact]
    public void Registro_ContextoSemConnectionString_FalhaNaPartida()
    {
        using var services = Build(s => s.AddWaybillDispatcher<OrdersDbContext>(), contextConnectionString: null);

        var error = Assert.Throws<OptionsValidationException>(() => services.GetRequiredService<IOptions<WaybillDispatcherOptions>>().Value);
        Assert.Contains("ConnectionString", error.Message);
    }

    private static ServiceProvider Build(Action<IServiceCollection> waybill, string? contextConnectionString = FromContext)
    {
        var services = new ServiceCollection()
            .AddLogging()
            .AddDbContext<OrdersDbContext>(o =>
            {
                if (contextConnectionString is null)
                    o.UseNpgsql(); // connection set later by the application, so none to read at startup
                else
                    o.UseNpgsql(contextConnectionString);
            });
        waybill(services);
        return services.BuildServiceProvider();
    }

    private sealed class OrdersDbContext(DbContextOptions<OrdersDbContext> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder) => modelBuilder.MapWaybillOutbox();
    }
}
