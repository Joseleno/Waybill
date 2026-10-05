using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Waybill.EntityFrameworkCore;
using Waybill.Testing;

namespace Waybill.Tests.Integration;

// The fake is only useful if it agrees with the real outbox. The same application code runs against both; the
// real outcome is read from PostgreSQL, the fake's from its Saved and Pending lists.
[Collection(PostgresCollection.Name)]
public sealed class FakeOutboxEquivalenceTests(PostgresFixture postgres)
{
    private sealed record Outcome(int Saved, int Pending, bool ReportedAtDispose);

    private static async Task PayInvoice(AppDbContext context, IOutbox<AppDbContext> outbox, string number, bool save)
    {
        var invoice = new Invoice { Number = number, Amount = 10m };
        context.Invoices.Add(invoice);
        outbox.Enqueue(new InvoicePaid(invoice.Id, invoice.Amount), key: invoice.Id.ToString());
        if (save)
            await context.SaveChangesAsync();
    }

    [Theory]
    [InlineData("salvo", "INV-1", true)]
    [InlineData("falha-no-save", "INV-0", true)] // INV-0 already exists: unique violation
    [InlineData("sem-save", "INV-1", false)]
    [InlineData("falha-e-clear", "INV-0", true, true)] // the application gives up: ChangeTracker.Clear() discards the message
    public async Task FakeOutbox_EquivalenteAoReal(string scenario, string number, bool save, bool clear = false)
    {
        var real = await RunReal(number, save, clear);
        var fake = await RunFake(number, save, clear);

        Assert.Equal(real, fake);
        Assert.Equal(scenario switch
        {
            "salvo" => new Outcome(1, 0, false),
            "falha-e-clear" => new Outcome(0, 0, false),
            _ => new Outcome(0, 1, true),
        }, real);
    }

    [Fact]
    public async Task FakeOutbox_EquivalenteAoReal_ValidacaoNoEnqueue()
    {
        var database = await TestDatabase.CreateAsync(postgres);
        await using var realServices = database.Services(o => o.MaxPayloadBytes = 16);
        await using var fakeServices = FakeServices(database, o => o.MaxPayloadBytes = 16);

        foreach (var services in new[] { realServices, fakeServices })
        {
            await using var scope = services.CreateAsyncScope();
            var outbox = scope.ServiceProvider.GetRequiredService<IOutbox<AppDbContext>>();

            Assert.Contains("is not registered", Assert.Throws<InvalidOperationException>(() => outbox.Enqueue(new Unregistered())).Message);
            Assert.Contains("above MaxPayloadBytes", Assert.Throws<InvalidOperationException>(() => outbox.Enqueue(new InvoicePaid(Guid.NewGuid(), 1m))).Message);
        }
    }

    [Fact]
    public async Task FakeOutbox_ShouldContain_DevolveAMensagemSalva()
    {
        var database = await TestDatabase.CreateAsync(postgres);
        await using var services = FakeServices(database);
        await using var scope = services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var fake = scope.ServiceProvider.GetRequiredService<FakeOutbox<AppDbContext>>();

        await PayInvoice(context, fake, "INV-1", save: true);

        Assert.Equal(10m, fake.ShouldContain<InvoicePaid>(m => m.Amount == 10m).Amount);
        Assert.Throws<WaybillAssertionException>(() => fake.ShouldContain<AuditRecorded>());
        Assert.Throws<WaybillAssertionException>(() => fake.ShouldBeEmpty());
    }

    private sealed record Unregistered;

    private async Task<Outcome> RunReal(string number, bool save, bool clear)
    {
        var database = await SeededDatabase();
        var logs = new LogSink();
        await using var services = database.Services(logs: logs);

        int pending;
        await using (var scope = services.CreateAsyncScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await Swallow(() => PayInvoice(context, scope.ServiceProvider.GetRequiredService<IOutbox<AppDbContext>>(), number, save));
            if (clear)
                context.ChangeTracker.Clear();
            // Measured, not inferred: outbox records the real context still has to insert.
            pending = context.ChangeTracker.Entries<OutboxRecord>().Count(e => e.State == EntityState.Added);
        }

        var reported = logs.Entries.Any(e => e.Level == LogLevel.Error && e.Category.StartsWith("Waybill", StringComparison.Ordinal)); // not EF's own log of the failed save
        var saved = (int)await database.OutboxCountAsync();
        return new Outcome(saved, pending, reported);
    }

    private async Task<Outcome> RunFake(string number, bool save, bool clear)
    {
        var database = await SeededDatabase();
        await using var services = FakeServices(database);

        var scope = services.CreateAsyncScope();
        var fake = scope.ServiceProvider.GetRequiredService<FakeOutbox<AppDbContext>>();
        await Swallow(() => PayInvoice(scope.ServiceProvider.GetRequiredService<AppDbContext>(), fake, number, save));
        if (clear)
            scope.ServiceProvider.GetRequiredService<AppDbContext>().ChangeTracker.Clear();
        var (saved, pending) = (fake.Saved.Count, fake.Pending.Count);

        var reported = false;
        try
        {
            await scope.DisposeAsync();
        }
        catch (WaybillAssertionException)
        {
            reported = true;
        }
        return new Outcome(saved, pending, reported);
    }

    private async Task<TestDatabase> SeededDatabase()
    {
        var database = await TestDatabase.CreateAsync(postgres);
        await using var services = database.Services();
        await using var scope = services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        context.Invoices.Add(new Invoice { Number = "INV-0", Amount = 1m });
        await context.SaveChangesAsync();
        return database;
    }

    private static ServiceProvider FakeServices(TestDatabase database, Action<WaybillOptions>? configure = null)
    {
        var services = new ServiceCollection()
            .AddLogging()
            .AddWaybill(o =>
            {
                o.MaxPayloadBytes = 64 * 1024;
                o.AddMessage("billing.invoice-paid.v1", TestJson.Default.InvoicePaid);
                configure?.Invoke(o);
            })
            .AddDbContext<AppDbContext>(o => o.UseNpgsql(database.ConnectionString))
            .AddFakeWaybillOutbox<AppDbContext>();
        return services.BuildServiceProvider();
    }

    private static async Task Swallow(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (DbUpdateException)
        {
        }
    }
}
