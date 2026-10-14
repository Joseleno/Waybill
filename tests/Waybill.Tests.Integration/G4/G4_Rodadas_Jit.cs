using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Waybill.EntityFrameworkCore.Dispatching;

namespace Waybill.Tests.Integration.G4;

// With a backlog, the claim's estimated cost passes jit_above_cost and PostgreSQL compiled it on every run: about half a
// second per claim with 200 thousand messages waiting, measured on PG 15 and 18 (8c-2 review). The dispatcher's own
// sessions run without JIT; options the application put in its connection string still apply, and win.
[Collection(PostgresCollection.Name)]
public sealed class G4_Rodadas_Jit(PostgresFixture postgres)
{
    [Fact]
    public async Task G4_Rodadas_DispatcherSemJit()
    {
        var ct = TestContext.Current.CancellationToken;
        var database = await TestDatabase.CreateAsync(postgres);
        var connectionString = new NpgsqlConnectionStringBuilder(database.ConnectionString) { Options = "-c statement_timeout=12345" }.ConnectionString;
        await using var services = new ServiceCollection()
            .AddLogging()
            .AddWaybill(o => o.MaxPayloadBytes = 1024)
            .AddWaybillDispatcher(o => o.ConnectionString = connectionString)
            .BuildServiceProvider();

        // Several times through the pool: the setting must survive the reset of a connection handed back.
        var dataSource = services.GetRequiredService<DispatcherDataSource>().Value;
        for (var i = 0; i < 3; i++)
        {
            await using var connection = await dataSource.OpenConnectionAsync(ct);
            await using var command = new NpgsqlCommand("SELECT current_setting('jit') || ' ' || current_setting('statement_timeout')", connection);
            Assert.Equal("off 12345ms", await command.ExecuteScalarAsync(ct));
        }
    }
}
