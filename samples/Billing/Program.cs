using System.Text.Json.Serialization;
using Billing;
using Microsoft.EntityFrameworkCore;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using RabbitMQ.Client;
using Waybill;
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
// Ordered by key: each invoice's messages go out in commit order, numbered, and Receipts checks the numbers.
builder.Services.AddWaybillDispatcher<BillingDbContext>(o => o.OrderByKey = true); // same database as the context
builder.Services.AddWaybillRetention<BillingDbContext>();
builder.Services.AddHealthChecks().AddWaybillDispatcherCheck();

// OpenTelemetry, in the sample only: Waybill reports through System.Diagnostics and depends on none of it. A payment's
// trace runs from the HTTP request through Waybill's create and send spans and RabbitMQ.Client's publish, on to
// Receipts. Listen to the incoming requests too: a request nobody samples takes the whole message trace with it. The
// exporter reads OTEL_EXPORTER_OTLP_ENDPOINT, the compose's Aspire dashboard.
builder.Services.AddOpenTelemetry()
    .ConfigureResource(resource => resource.AddService("billing"))
    .WithTracing(tracing => tracing
        .AddAspNetCoreInstrumentation()
        .AddSource(WaybillDiagnostics.ActivitySourceName, RabbitMQActivitySource.PublisherSourceName)
        .AddOtlpExporter())
    .WithMetrics(metrics => metrics.AddMeter(WaybillDiagnostics.MeterName).AddOtlpExporter())
    .WithLogging(logging => logging.AddOtlpExporter());

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
