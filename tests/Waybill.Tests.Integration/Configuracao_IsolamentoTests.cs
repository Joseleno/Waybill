using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;
using Waybill.EntityFrameworkCore.Retention;
using Waybill.Tests.Integration.G2;
using Waybill.Tests.Integration.Observabilidade;
using Waybill.Tests.Integration.Retencao;

namespace Waybill.Tests.Integration;

// The claim and the retention rely on READ COMMITTED (ADR 0001): under REPEATABLE READ a concurrent claim fails with
// 40001 instead of re-checking the row. They run as single autocommit statements, so the level is the session default;
// a database or role configured otherwise must stop them with a clear error, not leave them failing cycle after cycle.
[Collection(PostgresCollection.Name)]
public sealed class Configuracao_IsolamentoTests(PostgresFixture postgres)
{
    [Fact]
    public async Task Configuracao_IsolamentoDiferenteDeReadCommitted_DispatcherParaComErroCritico()
    {
        var ct = TestContext.Current.CancellationToken;
        var database = await TestDatabase.CreateAsync(postgres);
        await SetDefaultIsolationAsync(database, "repeatable read");
        var logs = new LogSink();
        await using var harness = new HealthHarness(database, new FakeTransport(), logs);

        await harness.Dispatcher.StartAsync(ct);
        var entry = await harness.WaitForAsync(HealthStatus.Unhealthy, ct);

        Assert.Contains("not running", entry.Description);
        Assert.Contains(logs.Entries, e => e.Level == LogLevel.Critical && e.Message.Contains("repeatable read") && e.Message.Contains("read committed"));
        await harness.Dispatcher.StopAsync(ct);
    }

    [Fact]
    public async Task Configuracao_IsolamentoDiferenteDeReadCommitted_RetencaoParaComErroCritico()
    {
        var ct = TestContext.Current.CancellationToken;
        var database = await TestDatabase.CreateAsync(postgres);
        await database.InsertPublishedOutboxBulkAsync(10, TimeSpan.FromDays(40));
        await SetDefaultIsolationAsync(database, "repeatable read");
        var logs = new LogSink();
        await using var services = new ServiceCollection()
            .AddLogging(logging => logging.AddProvider(logs))
            .AddWaybillRetention(o =>
            {
                o.ConnectionString = database.ConnectionString;
                o.Interval = TimeSpan.FromMilliseconds(100);
            })
            .BuildServiceProvider();
        var service = Assert.IsType<BackgroundService>(Assert.Single(services.GetServices<IHostedService>()), exactMatch: false);

        await service.StartAsync(ct);
        await service.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(30), ct); // it stops by itself

        Assert.Contains(logs.Entries, e => e.Level == LogLevel.Critical && e.Message.Contains("repeatable read"));
        Assert.Equal(10, await database.OutboxCountAsync()); // and deleted nothing
        await service.StopAsync(ct);
    }

    private async Task SetDefaultIsolationAsync(TestDatabase database, string level)
    {
        var name = new NpgsqlConnectionStringBuilder(database.ConnectionString).Database!;
        await using var connection = new NpgsqlConnection(postgres.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand($"ALTER DATABASE \"{name}\" SET default_transaction_isolation = '{level}'", connection);
        await command.ExecuteNonQueryAsync();
    }
}
