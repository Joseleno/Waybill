using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Waybill.EntityFrameworkCore;

namespace Waybill.Tests.Integration.G4;

// AddWaybillOutbox adds the key-lock interceptor to the context's options (ADR 0008). A context built by hand misses it:
// nothing is lost or reordered, but transactions enqueuing several keys may deadlock while ordering is on. The first
// Enqueue with a key logs it once, at Information, instead of failing: contexts built by hand are also how applications
// test with the fakes, and the producer cannot tell whether a dispatcher orders. Without a key there is no key lock.
[Collection(PostgresCollection.Name)]
public sealed class G4_ContextoSemInterceptor_AvisaUmaVez(PostgresFixture postgres)
{
    [Fact]
    public async Task G4_ContextoSemInterceptor_AvisaUmaVezEEnfileira()
    {
        var database = await TestDatabase.CreateAsync(postgres);
        var logs = new LogSink();
        var manual = new DbContextOptionsBuilder<HandBuiltDbContext>().UseNpgsql(database.ConnectionString).Options;
        await using var services = new ServiceCollection()
            .AddLogging(l => l.AddProvider(logs))
            .AddWaybill(o =>
            {
                o.MaxPayloadBytes = 1024;
                o.AddMessage("billing.invoice-paid.v1", TestJson.Default.InvoicePaid);
            })
            .AddScoped(_ => new HandBuiltDbContext(manual)) // not AddDbContext: the options never see the interceptor
            .AddWaybillOutbox<HandBuiltDbContext>()
            .BuildServiceProvider();

        for (var i = 0; i < 2; i++)
        {
            await using var scope = services.CreateAsyncScope();
            var context = scope.ServiceProvider.GetRequiredService<HandBuiltDbContext>();
            scope.ServiceProvider.GetRequiredService<IOutbox<HandBuiltDbContext>>().Enqueue(new InvoicePaid(Guid.NewGuid(), i), "invoice-1");
            await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        Assert.Equal(2, await database.ScalarAsync("SELECT count(*) FROM waybill.outbox"));
        var notices = logs.Entries.Where(e => e.Message.Contains("lacks the interceptor")).ToList();
        Assert.Single(notices);
        Assert.Equal(LogLevel.Information, notices[0].Level);
        Assert.Contains(nameof(HandBuiltDbContext), notices[0].Message);
    }

    // The notice is once per context type and process, so this test has a context type of its own.
    [Fact]
    public async Task G4_ContextoSemInterceptor_SemChave_NaoAvisa()
    {
        var database = await TestDatabase.CreateAsync(postgres);
        var logs = new LogSink();
        var manual = new DbContextOptionsBuilder<UnkeyedDbContext>().UseNpgsql(database.ConnectionString).Options;
        await using var services = new ServiceCollection()
            .AddLogging(l => l.AddProvider(logs))
            .AddWaybill(o =>
            {
                o.MaxPayloadBytes = 1024;
                o.AddMessage("billing.invoice-paid.v1", TestJson.Default.InvoicePaid);
            })
            .AddScoped(_ => new UnkeyedDbContext(manual))
            .AddWaybillOutbox<UnkeyedDbContext>()
            .BuildServiceProvider();

        foreach (var key in new[] { null, null, "" })
        {
            await using var scope = services.CreateAsyncScope();
            var context = scope.ServiceProvider.GetRequiredService<UnkeyedDbContext>();
            scope.ServiceProvider.GetRequiredService<IOutbox<UnkeyedDbContext>>().Enqueue(new InvoicePaid(Guid.NewGuid(), 1m), key);
            await context.SaveChangesAsync(TestContext.Current.CancellationToken);
            if (key is null)
                Assert.DoesNotContain(logs.Entries, e => e.Message.Contains("lacks the interceptor"));
        }

        Assert.Single(logs.Entries, e => e.Message.Contains("lacks the interceptor")); // an empty key is a key, and locked
    }

    [Fact]
    public async Task G4_ContextoRegistradoComAddDbContext_TemOInterceptorESemAviso()
    {
        var database = await TestDatabase.CreateAsync(postgres);
        var logs = new LogSink();
        await using var services = database.Services(logs: logs);
        await using var scope = services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        scope.ServiceProvider.GetRequiredService<IOutbox<AppDbContext>>().Enqueue(new InvoicePaid(Guid.NewGuid(), 1m), "invoice-1");
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        Assert.DoesNotContain(logs.Entries, e => e.Message.Contains("lacks the interceptor"));
    }

    public sealed class HandBuiltDbContext(DbContextOptions<HandBuiltDbContext> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder) => modelBuilder.MapWaybillOutbox();
    }

    public sealed class UnkeyedDbContext(DbContextOptions<UnkeyedDbContext> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder) => modelBuilder.MapWaybillOutbox();
    }
}
