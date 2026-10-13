using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Waybill.EntityFrameworkCore;

namespace Waybill.Tests.Integration.G4;

// An application role allowed only to insert into the outbox keeps enqueuing once ordering is on: the numbering trigger
// runs with the rights of the role that migrated the schema (SECURITY DEFINER), so it reads settings and writes
// outbox_keys for it.
[Collection(PostgresCollection.Name)]
public sealed class G4_PapelSoComInsert_Enfileira(PostgresFixture postgres)
{
    [Fact]
    public async Task G4_PapelSoComInsert_EnfileiraComSequence()
    {
        var database = await TestDatabase.CreateAsync(postgres);
        await KeyLockHarness.EnableOrderingAsync(database);
        var role = $"waybill_app_{Guid.NewGuid():N}"[..30];
        await database.ExecuteAsync($"""
            CREATE ROLE {role} LOGIN PASSWORD 'app';
            GRANT USAGE ON SCHEMA waybill TO {role};
            GRANT INSERT ON waybill.outbox TO {role};
            """);
        var asApp = new NpgsqlConnectionStringBuilder(database.ConnectionString) { Username = role, Password = "app" }.ConnectionString;

        await using (var services = database.Services(sharedConnection: _ => new NpgsqlConnection(asApp)))
        {
            await using var scope = services.CreateAsyncScope();
            var outbox = scope.ServiceProvider.GetRequiredService<IOutbox<AppDbContext>>();
            outbox.Enqueue(new InvoicePaid(Guid.NewGuid(), 1m), "K1");
            outbox.Enqueue(new InvoicePaid(Guid.NewGuid(), 1m), "K2"); // with the key list
            await scope.ServiceProvider.GetRequiredService<AppDbContext>().SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        Assert.Equal(["K1:1:1:1:1", "K2:1:1:1:1"], await KeyLockHarness.SequencesAsync(database));
        NpgsqlConnection.ClearAllPools();
        await database.ExecuteAsync($"DROP OWNED BY {role}; DROP ROLE {role}");
    }
}
