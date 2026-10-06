using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

// No `using Waybill...` on purpose (ADR 0005): registering and mapping Waybill must need only the namespaces an
// application already imports. The compiler is the test; the assertions check that everything got registered.
namespace AnApplication; // outside the Waybill namespace, which would otherwise be in scope

public sealed class RegistroTests
{
    [Fact]
    public void Registro_SoComUsingDeDependencyInjection_RegistraTudo()
    {
        var services = new ServiceCollection()
            .AddLogging()
            .AddWaybill(o => o.MaxPayloadBytes = 1024)
            .AddWaybillOutbox<OrdersDbContext>()
            .AddWaybillInbox<OrdersDbContext>()
            .AddWaybillRabbitMQ(o =>
            {
                o.Uri = new Uri("amqp://localhost");
                o.Exchange = "events";
            })
            .AddWaybillDispatcher(o => o.ConnectionString = "Host=localhost")
            .AddWaybillRetention(o => o.ConnectionString = "Host=localhost");
        services.AddHealthChecks().AddWaybillDispatcherCheck();

        Assert.Contains(services, s => s.ServiceType == typeof(Waybill.EntityFrameworkCore.IOutbox<OrdersDbContext>));
        Assert.Contains(services, s => s.ServiceType == typeof(Waybill.EntityFrameworkCore.IInbox<OrdersDbContext>));
        Assert.Contains(services, s => s.ServiceType == typeof(Waybill.ITransport));
        Assert.Equal(3, services.Count(s => s.ServiceType == typeof(IHostedService) && s.ImplementationType?.Namespace?.StartsWith("Waybill", StringComparison.Ordinal) == true)); // dispatcher, gauge sampling, retention
    }

    private sealed class OrdersDbContext(DbContextOptions<OrdersDbContext> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder) => modelBuilder.MapWaybillOutbox();
    }
}
