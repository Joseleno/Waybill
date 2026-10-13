using Microsoft.Extensions.DependencyInjection;
using Waybill.EntityFrameworkCore;
using Waybill.EntityFrameworkCore.Dispatching;

namespace Waybill.Tests.Integration.G4;

// Ordering is off by default, and then none of it runs (SPEC, cost criterion): keyed messages are not numbered, the
// counters are not written, the key list is dropped by the trigger, and the claim reads none of ordering's tables
// beyond the one-row settings guard.
[Collection(PostgresCollection.Name)]
public sealed class G4_OrdenacaoDesligada_SemContadorNemFiltro(PostgresFixture postgres)
{
    [Fact]
    public async Task G4_OrdenacaoDesligada_NadaNumeradoNemGravadoEmOutboxKeys()
    {
        var database = await TestDatabase.CreateAsync(postgres);
        await using var services = database.Services();
        await using var scope = services.CreateAsyncScope();
        var outbox = scope.ServiceProvider.GetRequiredService<IOutbox<AppDbContext>>();
        foreach (var key in new[] { "order-1", "order-2", "order-1" }) // two keys: the interceptor sends the list
            outbox.Enqueue(new InvoicePaid(Guid.NewGuid(), 1m), key);
        await scope.ServiceProvider.GetRequiredService<AppDbContext>().SaveChangesAsync(TestContext.Current.CancellationToken);

        Assert.Equal(3, await database.ScalarAsync("SELECT count(*) FROM waybill.outbox WHERE sequence IS NULL AND lock_keys IS NULL"));
        Assert.Equal(0, await database.ScalarAsync("SELECT count(*) FROM waybill.outbox_keys"));
    }

    [Fact]
    public void G4_OrdenacaoDesligada_ClaimSemTabelasDaOrdenacaoAlemDaGuarda()
    {
        Assert.DoesNotContain("outbox_keys", OutboxStore.ClaimSql);
        Assert.DoesNotContain("outbox_partitions", OutboxStore.ClaimSql);
        Assert.DoesNotContain("sequence", OutboxStore.ClaimSql);
        Assert.Equal(2, System.Text.RegularExpressions.Regex.Matches(OutboxStore.ClaimSql, @"NOT EXISTS \(SELECT 1 FROM waybill\.settings\)").Count);
    }
}
