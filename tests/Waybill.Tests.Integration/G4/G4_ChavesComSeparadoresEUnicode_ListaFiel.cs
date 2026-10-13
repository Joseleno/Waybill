using Microsoft.Extensions.DependencyInjection;
using Waybill.EntityFrameworkCore;

namespace Waybill.Tests.Integration.G4;

// Keys are the application's data. The list travels as a text[] parameter, never as SQL text, so separators, quotes,
// braces and any Unicode reach the counters as written; a key misread there would become a counter never cleaned.
[Collection(PostgresCollection.Name)]
public sealed class G4_ChavesComSeparadoresEUnicode_ListaFiel(PostgresFixture postgres)
{
    [Fact]
    public async Task G4_ChavesComSeparadoresEUnicode_ContadoresExatamenteAsChaves()
    {
        var database = await TestDatabase.CreateAsync(postgres);
        await KeyLockHarness.EnableOrderingAsync(database);
        string[] keys = ["a,b", "\"aspas\"", "barra\\invertida", "{chaves}", "it's", "NULL", "ção-日本語-🙂", new string('x', 255)];
        await using var services = database.Services();
        await using var scope = services.CreateAsyncScope();
        var outbox = scope.ServiceProvider.GetRequiredService<IOutbox<AppDbContext>>();
        foreach (var key in keys)
            outbox.Enqueue(new InvoicePaid(Guid.NewGuid(), 1m), key);
        await scope.ServiceProvider.GetRequiredService<AppDbContext>().SaveChangesAsync(TestContext.Current.CancellationToken);

        var expected = keys.Order(StringComparer.Ordinal).Select(k => $"{k}:1:1:1:1").ToArray();
        Assert.Equal(expected, await KeyLockHarness.SequencesAsync(database));
        Assert.Equal(0, await database.ScalarAsync("SELECT count(*) FROM waybill.outbox WHERE lock_keys IS NOT NULL"));
    }
}
