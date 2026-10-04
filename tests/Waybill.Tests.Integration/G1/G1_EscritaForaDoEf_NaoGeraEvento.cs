using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Waybill.EntityFrameworkCore;

namespace Waybill.Tests.Integration.G1;

// Documented limit: only SaveChanges carries enqueued messages. ExecuteUpdate, ExecuteDelete and raw SQL write
// data without them, and a message enqueued around such a write stays pending (and is reported).
[Collection(PostgresCollection.Name)]
public sealed class G1_EscritaForaDoEf_NaoGeraEvento(PostgresFixture postgres)
{
    [Fact]
    public async Task G1_EscritaForaDoEf_NaoGeraEvento_ExecuteUpdateESqlCru()
    {
        var ct = TestContext.Current.CancellationToken;
        var database = await TestDatabase.CreateAsync(postgres);
        var logs = new LogSink();
        await using var services = database.Services(logs: logs);

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

            outbox.Enqueue(new InvoicePaid(Guid.NewGuid(), 2m));
            await context.Invoices.ExecuteUpdateAsync(s => s.SetProperty(i => i.Amount, 2m), ct);
            await context.Database.ExecuteSqlRawAsync("UPDATE invoices SET \"Amount\" = 3", ct);
            await context.Invoices.Where(i => i.Number == "nobody").ExecuteDeleteAsync(ct);
        }

        Assert.Equal(3, await database.ScalarAsync("SELECT \"Amount\"::int FROM invoices"));
        Assert.Equal(0, await database.OutboxCountAsync());
        Assert.Single(logs.Entries, e => e.Level == LogLevel.Error);
    }
}
