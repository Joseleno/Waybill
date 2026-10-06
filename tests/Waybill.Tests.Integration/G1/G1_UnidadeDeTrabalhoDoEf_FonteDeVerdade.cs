using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Waybill.EntityFrameworkCore;

namespace Waybill.Tests.Integration.G1;

// The outbox record lives in the DbContext's change tracker from the moment it is enqueued, so whatever the
// application does to its unit of work happens to the event too. A separate buffer broke these cases: Clear()
// after a failure re-inserted the event without its data, and an Enqueue during SavingChanges was dropped.
[Collection(PostgresCollection.Name)]
public sealed class G1_UnidadeDeTrabalhoDoEf_FonteDeVerdade(PostgresFixture postgres)
{
    [Fact]
    public async Task G1_ChangeTrackerClearAposFalha_SemEventoFantasma()
    {
        var ct = TestContext.Current.CancellationToken;
        var database = await TestDatabase.CreateAsync(postgres);
        await using var services = database.Services();
        await using (var seed = services.CreateAsyncScope())
        {
            var context = seed.ServiceProvider.GetRequiredService<AppDbContext>();
            context.Invoices.Add(new Invoice { Number = "INV-1", Amount = 1m });
            await context.SaveChangesAsync(ct);
        }

        await using (var scope = services.CreateAsyncScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var outbox = scope.ServiceProvider.GetRequiredService<IOutbox<AppDbContext>>();

            var duplicate = new Invoice { Number = "INV-1", Amount = 10m };
            context.Invoices.Add(duplicate);
            outbox.Enqueue(new InvoicePaid(duplicate.Id, duplicate.Amount));
            await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync(ct));

            context.ChangeTracker.Clear(); // the application gives up on this unit of work...
            context.Invoices.Add(new Invoice { Number = "INV-2", Amount = 2m }); // ...and saves something else
            await context.SaveChangesAsync(ct);
        }

        Assert.Equal(0, await database.OutboxCountAsync());
        Assert.Equal(2, await database.ScalarAsync("SELECT count(*) FROM invoices"));
    }

    [Fact]
    public async Task G1_EnqueueDuranteSavingChanges_EntraNoMesmoSave()
    {
        var ct = TestContext.Current.CancellationToken;
        var database = await TestDatabase.CreateAsync(postgres);
        var raiser = new DomainEventRaiser();
        await using var services = database.Services(interceptors: raiser);

        await using (var scope = services.CreateAsyncScope())
        {
            raiser.Outbox = scope.ServiceProvider.GetRequiredService<IOutbox<AppDbContext>>();
            var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            context.Invoices.Add(new Invoice { Number = "INV-1", Amount = 10m });
            await context.SaveChangesAsync(ct);
        }

        Assert.Equal(1, await database.OutboxCountAsync());
        Assert.Equal(1, await database.ScalarAsync("SELECT count(*) FROM invoices"));
    }

    [Fact]
    public async Task G1_ContextoDoPool_PendenteNaoVazaParaOProximoEscopo()
    {
        var ct = TestContext.Current.CancellationToken;
        var database = await TestDatabase.CreateAsync(postgres);
        var logs = new LogSink();
        var services = new ServiceCollection()
            .AddLogging(l => l.AddProvider(logs))
            .AddWaybill(o =>
            {
                o.MaxPayloadBytes = 1024;
                o.AddMessage("billing.invoice-paid.v1", TestJson.Default.InvoicePaid);
            })
            .AddDbContextPool<AppDbContext>(o => o.UseNpgsql(database.ConnectionString), poolSize: 1)
            .AddWaybillOutbox<AppDbContext>();
        await using var provider = services.BuildServiceProvider();

        await using (var first = provider.CreateAsyncScope())
        {
            first.ServiceProvider.GetRequiredService<IOutbox<AppDbContext>>().Enqueue(new InvoicePaid(Guid.NewGuid(), 1m));
        } // never saved; the context goes back to the pool

        await using (var second = provider.CreateAsyncScope())
        {
            var context = second.ServiceProvider.GetRequiredService<AppDbContext>();
            context.Invoices.Add(new Invoice { Number = "INV-1", Amount = 1m });
            await context.SaveChangesAsync(ct);
        }

        Assert.Equal(0, await database.OutboxCountAsync());
        Assert.Single(logs.Entries, e => e.Level == LogLevel.Error);
    }

    // A domain-events interceptor registered after Waybill's setup, enqueuing while the save is starting.
    private sealed class DomainEventRaiser : SaveChangesInterceptor
    {
        public IOutbox<AppDbContext>? Outbox { get; set; }

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            foreach (var invoice in eventData.Context!.ChangeTracker.Entries<Invoice>().Where(e => e.State == EntityState.Added).ToList())
                Outbox!.Enqueue(new InvoicePaid(invoice.Entity.Id, invoice.Entity.Amount));
            return ValueTask.FromResult(result);
        }
    }
}
