using Microsoft.EntityFrameworkCore;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using RabbitMQ.Client;
using Receipts;
using Waybill;
using Waybill.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);
var database = builder.Configuration.GetConnectionString("Receipts")
    ?? throw new InvalidOperationException("ConnectionStrings:Receipts is required.");
var broker = new Uri(builder.Configuration.GetConnectionString("RabbitMQ")
    ?? throw new InvalidOperationException("ConnectionStrings:RabbitMQ is required."));

builder.Services.AddDbContext<ReceiptsDbContext>(o => o.UseNpgsql(database));
builder.Services.AddSingleton(new BrokerSettings(broker));
builder.Services.AddScoped<ReceiptIssuer>();
builder.Services.AddScoped<InvoicePaidHandler>();
builder.Services.AddHostedService<InvoicePaidConsumer>();

// Waybill: the inbox around the handler, and the outbox for the event this consumer publishes in turn.
builder.Services.AddWaybill(ReceiptsMessages.Register);
builder.Services.AddWaybillInbox<ReceiptsDbContext>();
builder.Services.AddWaybillOutbox<ReceiptsDbContext>();
builder.Services.AddWaybillRabbitMQ(o =>
{
    o.Uri = broker;
    o.Exchange = "events";
});
builder.Services.AddWaybillDispatcher<ReceiptsDbContext>();
builder.Services.AddWaybillRetention<ReceiptsDbContext>();
builder.Services.AddHealthChecks().AddWaybillDispatcherCheck();

// OpenTelemetry, in the sample only (see Billing). RabbitMQ.Client's subscriber source opens a deliver span around each
// delivery, child of the publisher's; the inbox's process span hangs from it, in the payment's trace.
builder.Services.AddOpenTelemetry()
    .ConfigureResource(resource => resource.AddService("receipts"))
    .WithTracing(tracing => tracing
        .AddAspNetCoreInstrumentation()
        .AddSource(WaybillDiagnostics.ActivitySourceName, RabbitMQActivitySource.PublisherSourceName, RabbitMQActivitySource.SubscriberSourceName)
        .AddOtlpExporter())
    .WithMetrics(metrics => metrics.AddMeter(WaybillDiagnostics.MeterName).AddOtlpExporter())
    .WithLogging(logging => logging.AddOtlpExporter());

var app = builder.Build();

// `dotnet Receipts.dll migrate` applies the schema and exits (see the Billing service).
if (args is ["migrate"])
{
    await WaybillSchema.MigrateAsync(database);
    await using var scope = app.Services.CreateAsyncScope();
    await scope.ServiceProvider.GetRequiredService<ReceiptsDbContext>().Database.MigrateAsync();
    return;
}

app.MapGet("/receipts", async (Guid? invoiceId, ReceiptsDbContext db, CancellationToken ct) =>
    await db.Receipts.Where(r => invoiceId == null || r.InvoiceId == invoiceId).OrderByDescending(r => r.IssuedAt).Take(100).ToListAsync(ct));

app.MapHealthChecks("/health");

await app.RunAsync();
