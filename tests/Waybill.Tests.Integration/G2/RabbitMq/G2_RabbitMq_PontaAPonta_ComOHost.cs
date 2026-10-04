using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Waybill.EntityFrameworkCore;
using Waybill.EntityFrameworkCore.Dispatching;
using Waybill.RabbitMQ;

namespace Waybill.Tests.Integration.G2.RabbitMq;

// The package seen from outside: only the public API, the way an application wires it. An event written in the
// same transaction as the invoice reaches the queue.
[Collection(BrokerCollection.Name)]
public sealed class G2_RabbitMq_PontaAPonta_ComOHost(PostgresFixture postgres, RabbitMqFixture rabbit)
{
    [Fact]
    public async Task G2_RabbitMq_PontaAPonta_ComOHost_EventoGravadoComODadoChegaAFila()
    {
        var ct = TestContext.Current.CancellationToken;
        var database = await TestDatabase.CreateAsync(postgres);
        var (exchange, queue) = await rabbit.DeclareTopologyAsync("billing.#");

        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddWaybill(o =>
        {
            o.MaxPayloadBytes = 64 * 1024;
            o.AddMessage("billing.invoice-paid.v1", TestJson.Default.InvoicePaid);
        });
        builder.Services.AddDbContext<AppDbContext>(db => db.UseNpgsql(database.ConnectionString));
        builder.Services.AddWaybillOutbox<AppDbContext>();
        builder.Services.AddWaybillDispatcher(o =>
        {
            o.ConnectionString = database.ConnectionString;
            o.PollingInterval = TimeSpan.FromMilliseconds(100);
        });
        builder.Services.AddWaybillRabbitMQ(o =>
        {
            o.Uri = rabbit.Uri;
            o.Exchange = exchange;
        });
        using var host = builder.Build();
        await host.StartAsync(ct);

        Guid messageId;
        await using (var scope = host.Services.CreateAsyncScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var invoice = new Invoice { Number = "INV-1", Amount = 10m };
            context.Invoices.Add(invoice);
            messageId = scope.ServiceProvider.GetRequiredService<IOutbox<AppDbContext>>()
                .Enqueue(new InvoicePaid(invoice.Id, invoice.Amount), key: invoice.Id.ToString());
            await context.SaveChangesAsync(ct);
        }

        var delivered = new List<global::RabbitMQ.Client.BasicGetResult>();
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (delivered.Count == 0 && DateTime.UtcNow < deadline)
        {
            delivered.AddRange(await rabbit.DrainAsync(queue));
            await Task.Delay(100, ct);
        }
        await host.StopAsync(ct);

        Assert.Equal(messageId.ToString(), Assert.Single(delivered).BasicProperties.MessageId);
        Assert.Equal(1, await database.CountAsync("published"));
    }
}
