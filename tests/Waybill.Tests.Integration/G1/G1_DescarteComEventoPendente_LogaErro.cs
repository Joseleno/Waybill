using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Waybill.EntityFrameworkCore;

namespace Waybill.Tests.Integration.G1;

// An enqueued message that is never saved is reported at the end of the scope: an error log by default, an
// exception only when explicitly asked for.
[Collection(PostgresCollection.Name)]
public sealed class G1_DescarteComEventoPendente_LogaErro(PostgresFixture postgres)
{
    [Fact]
    public async Task G1_DescarteComEventoPendente_LogaErro_SemExcecao()
    {
        var database = await TestDatabase.CreateAsync(postgres);
        var logs = new LogSink();
        await using var services = database.Services(logs: logs);

        await using (var scope = services.CreateAsyncScope())
        {
            var outbox = scope.ServiceProvider.GetRequiredService<IOutbox<AppDbContext>>();
            outbox.Enqueue(new InvoicePaid(Guid.NewGuid(), 10m));
        } // no SaveChanges

        var error = Assert.Single(logs.Entries, e => e.Level == LogLevel.Error);
        Assert.Contains("1 message(s) were enqueued on AppDbContext but never saved", error.Message);
        Assert.Equal(0, await database.OutboxCountAsync());
    }

    [Fact]
    public async Task G1_DescarteComEventoPendente_ComOpcaoLigada_Lanca()
    {
        var database = await TestDatabase.CreateAsync(postgres);
        await using var services = database.Services(o => o.ThrowOnPendingMessagesAtDispose = true);

        var scope = services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await context.Database.BeginTransactionAsync(TestContext.Current.CancellationToken);
        scope.ServiceProvider.GetRequiredService<IOutbox<AppDbContext>>().Enqueue(new InvoicePaid(Guid.NewGuid(), 10m));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(async () => await scope.DisposeAsync());
        Assert.Contains("never saved", error.Message);

        // The scope stops disposing at the first exception; the context (and its open transaction) must not leak.
        Assert.Throws<ObjectDisposedException>(() => context.Model);
        Assert.Equal(0, await database.ScalarAsync(
            "SELECT count(*) FROM pg_stat_activity WHERE datname = current_database() AND state LIKE 'idle in transaction%'"));
    }

    [Fact]
    public async Task G1_DescarteDepoisDeSalvar_NaoReporta()
    {
        var ct = TestContext.Current.CancellationToken;
        var database = await TestDatabase.CreateAsync(postgres);
        var logs = new LogSink();
        await using var services = database.Services(logs: logs);

        await using (var scope = services.CreateAsyncScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            scope.ServiceProvider.GetRequiredService<IOutbox<AppDbContext>>().Enqueue(new InvoicePaid(Guid.NewGuid(), 10m));
            await context.SaveChangesAsync(ct);
        }

        Assert.DoesNotContain(logs.Entries, e => e.Level >= LogLevel.Error);
        Assert.Equal(1, await database.OutboxCountAsync());
    }
}
