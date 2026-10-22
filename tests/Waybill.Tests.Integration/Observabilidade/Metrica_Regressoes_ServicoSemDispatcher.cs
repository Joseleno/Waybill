using System.Diagnostics.Metrics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Waybill.EntityFrameworkCore;
using Waybill.Tests.Integration.G4;

namespace Waybill.Tests.Integration.Observabilidade;

// waybill.inbox.regressions counts the messages the sequence check refused, by handler and reason. A service that only
// consumes has no dispatcher, so the inbox registers its own instruments; and once, however many contexts it serves.
[Collection(PostgresCollection.Name)]
public sealed class Metrica_Regressoes_ServicoSemDispatcher(PostgresFixture postgres)
{
    [Fact]
    public async Task Metrica_Regressoes_ServicoSemDispatcher_ContaPorHandlerEMotivo()
    {
        var database = await TestDatabase.CreateAsync(postgres);
        var collection = new ServiceCollection()
            .AddLogging()
            .AddMetrics()
            .AddWaybill(o =>
            {
                o.MaxPayloadBytes = 1024;
                o.AddMessage("billing.invoice-paid.v1", TestJson.Default.InvoicePaid);
            });
        collection.AddDbContext<AppDbContext>(o => o.UseNpgsql(database.ConnectionString));
        collection.AddDbContext<AuditDbContext>(o => o.UseNpgsql(database.ConnectionString));
        collection.AddWaybillInbox<AppDbContext>().AddWaybillInbox<AuditDbContext>(); // no AddWaybillDispatcher
        await using var services = collection.BuildServiceProvider();
        var factory = services.GetRequiredService<IMeterFactory>();
        var regressions = new List<(long Value, Dictionary<string, object?> Tags)>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Name == "waybill.inbox.regressions" && ReferenceEquals(instrument.Meter.Scope, factory))
                l.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((_, value, tags, _) =>
            regressions.Add((value, tags.ToArray().ToDictionary(t => t.Key, t => t.Value))));
        listener.Start();

        await InboxOrderHarness.DeliverAsync(services, Guid.CreateVersion7(), 2);
        Assert.Equal(OrderedInboxResult.Regressed, await InboxOrderHarness.DeliverAsync(services, Guid.CreateVersion7(), 1));

        var (count, tagged) = Assert.Single(regressions);
        Assert.Equal(1, count);
        Assert.Equal(InboxOrderHarness.Handler, tagged["waybill.inbox.handler"]);
        Assert.Equal("lower", tagged["waybill.inbox.regression"]);
        Assert.Single(collection, d => d.ServiceType.Name == "InboxMetrics");
    }
}
