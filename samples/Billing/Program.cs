using Billing;
using Microsoft.EntityFrameworkCore;
using Waybill;
using Waybill.EntityFrameworkCore;
using Waybill.EntityFrameworkCore.Dispatching;
using Waybill.EntityFrameworkCore.Retention;
using Waybill.RabbitMQ;

var builder = WebApplication.CreateBuilder(args);
var database = builder.Configuration.GetConnectionString("Billing")
    ?? throw new InvalidOperationException("ConnectionStrings:Billing is required.");
var broker = builder.Configuration.GetConnectionString("RabbitMQ")
    ?? throw new InvalidOperationException("ConnectionStrings:RabbitMQ is required.");

builder.Services.AddDbContext<BillingDbContext>(o => o.UseNpgsql(database));
builder.Services.AddScoped<InvoicePayments>();
builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter()));

// Waybill: messages, the outbox on this context, the dispatcher with its transport, retention and the health check.
builder.Services.AddWaybill(BillingMessages.Register);
builder.Services.AddWaybillOutbox<BillingDbContext>();
builder.Services.AddWaybillRabbitMQ(o =>
{
    o.Uri = new Uri(broker);
    o.Exchange = "events";
});
builder.Services.AddWaybillDispatcher(o => o.ConnectionString = database);
builder.Services.AddWaybillRetention(o => o.ConnectionString = database);
builder.Services.AddHealthChecks().AddWaybillDispatcher();

var app = builder.Build();

// `dotnet Billing.dll migrate` applies the schema and exits. It runs as its own step (the compose `migrate` service),
// never from every API instance at startup: instances would race each other and need DDL rights they should not have.
if (args is ["migrate"])
{
    await WaybillSchema.MigrateAsync(database);
    await using var scope = app.Services.CreateAsyncScope();
    await scope.ServiceProvider.GetRequiredService<BillingDbContext>().Database.MigrateAsync();
    return;
}

app.MapPost("/invoices", async (NewInvoice request, BillingDbContext db, CancellationToken ct) =>
{
    var invoice = new Invoice { Number = request.Number, Amount = request.Amount };
    db.Invoices.Add(invoice);
    await db.SaveChangesAsync(ct);
    return Results.Created($"/invoices/{invoice.Id}", invoice);
});

app.MapGet("/invoices/{id:guid}", async (Guid id, BillingDbContext db, CancellationToken ct) =>
    await db.Invoices.FindAsync([id], ct) is { } invoice ? Results.Ok(invoice) : Results.NotFound());

app.MapPost("/invoices/{id:guid}/payments", async (Guid id, InvoicePayments payments, CancellationToken ct) =>
    await payments.PayAsync(id, ct) switch
    {
        PaymentResult.Paid => Results.Ok(),
        PaymentResult.AlreadyPaid => Results.Conflict("The invoice is already paid."),
        _ => Results.NotFound(),
    });

app.MapHealthChecks("/health");

await app.RunAsync();

internal sealed record NewInvoice(string Number, decimal Amount);
