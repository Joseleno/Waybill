using System.Text.Json.Serialization;
using Billing;
using Microsoft.EntityFrameworkCore;
using Waybill.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);
var database = builder.Configuration.GetConnectionString("Billing")
    ?? throw new InvalidOperationException("ConnectionStrings:Billing is required.");
var broker = builder.Configuration.GetConnectionString("RabbitMQ")
    ?? throw new InvalidOperationException("ConnectionStrings:RabbitMQ is required.");

builder.Services.AddDbContext<BillingDbContext>(o => o.UseNpgsql(database));
builder.Services.AddScoped<InvoicePayments>();
builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));

// Waybill. AddWaybill registers the messages this service publishes; AddWaybillOutbox lets the context enqueue them
// (its model maps the table with modelBuilder.MapWaybillOutbox()); the dispatcher publishes them through the RabbitMQ
// transport; retention deletes what was delivered; the health check reports on the dispatcher.
builder.Services.AddWaybill(BillingMessages.Register);
builder.Services.AddWaybillOutbox<BillingDbContext>();
builder.Services.AddWaybillRabbitMQ(o =>
{
    o.Uri = new Uri(broker);
    o.Exchange = "events";
});
builder.Services.AddWaybillDispatcher<BillingDbContext>(); // same database as the context
builder.Services.AddWaybillRetention<BillingDbContext>();
builder.Services.AddHealthChecks().AddWaybillDispatcherCheck();

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
    if (string.IsNullOrWhiteSpace(request.Number) || request.Amount <= 0)
        return Results.BadRequest("A number and a positive amount are required.");

    var invoice = new Invoice { Number = request.Number, Amount = request.Amount };
    db.Invoices.Add(invoice);
    try
    {
        await db.SaveChangesAsync(ct);
    }
    catch (DbUpdateException exception) when (exception.InnerException is Npgsql.PostgresException { SqlState: Npgsql.PostgresErrorCodes.UniqueViolation })
    {
        return Results.Conflict($"Invoice {request.Number} already exists.");
    }
    return Results.Created($"/invoices/{invoice.Id}", invoice);
});

app.MapGet("/invoices/{id:guid}", async (Guid id, BillingDbContext db, CancellationToken ct) =>
    await db.Invoices.FindAsync([id], ct) is { } invoice ? Results.Ok(invoice) : Results.NotFound());

app.MapPost("/invoices/{id:guid}/payments", async (Guid id, InvoicePayments payments, BillingDbContext db, CancellationToken ct) =>
    await payments.PayAsync(id, ct) switch
    {
        PaymentResult.Paid => Results.Ok(await db.Invoices.FindAsync([id], ct)),
        PaymentResult.AlreadyPaid => Results.Conflict("The invoice is already paid."),
        _ => Results.NotFound(),
    });

app.MapHealthChecks("/health");

await app.RunAsync();

internal sealed record NewInvoice(string? Number, decimal Amount);
