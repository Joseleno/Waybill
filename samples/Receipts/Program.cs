using Microsoft.EntityFrameworkCore;
using Receipts;
using Waybill;
using Waybill.EntityFrameworkCore;
using Waybill.EntityFrameworkCore.Dispatching;
using Waybill.EntityFrameworkCore.Retention;
using Waybill.RabbitMQ;

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
builder.Services.AddWaybillDispatcher(o => o.ConnectionString = database);
builder.Services.AddWaybillRetention(o => o.ConnectionString = database);
builder.Services.AddHealthChecks().AddWaybillDispatcher();

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
    await db.Receipts.Where(r => invoiceId == null || r.InvoiceId == invoiceId).ToListAsync(ct));

app.MapHealthChecks("/health");

await app.RunAsync();
