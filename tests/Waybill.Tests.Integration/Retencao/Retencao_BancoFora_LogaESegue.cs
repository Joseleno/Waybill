using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;
using Waybill.EntityFrameworkCore.Retention;

namespace Waybill.Tests.Integration.Retencao;

// The cleanup runs as a hosted service. The database going away must not take the host down: the pass fails, is
// logged, and the next one cleans up once the database is back.
[Collection(PostgresCollection.Name)]
public sealed class Retencao_BancoFora_LogaESegue(PostgresFixture postgres)
{
    [Fact]
    public async Task Retencao_BancoFora_LogaErroEVoltaALimparQuandoOBancoVolta()
    {
        var ct = TestContext.Current.CancellationToken;
        var database = await TestDatabase.CreateAsync(postgres);
        await database.InsertPublishedOutboxBulkAsync(10, TimeSpan.FromDays(40));
        var name = new NpgsqlConnectionStringBuilder(database.ConnectionString).Database!;
        var logs = new LogSink();
        await using var services = new ServiceCollection()
            .AddLogging(logging => logging.AddProvider(logs))
            .AddWaybillRetention(o =>
            {
                o.ConnectionString = database.ConnectionString;
                o.Interval = TimeSpan.FromMilliseconds(200);
            })
            .BuildServiceProvider();
        var service = Assert.Single(services.GetServices<IHostedService>());

        await AdminAsync($"ALTER DATABASE \"{name}\" ALLOW_CONNECTIONS false");
        await AdminAsync($"SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE datname = '{name}'");
        NpgsqlConnection.ClearAllPools(); // the test's own pooled connections to that database were terminated too
        await service.StartAsync(ct);
        await WaitUntilAsync(() => Task.FromResult(logs.Entries.Any(e => e.Level == LogLevel.Error && e.Category.Contains("Retention"))), ct);
        await AdminAsync($"ALTER DATABASE \"{name}\" ALLOW_CONNECTIONS true");
        await WaitUntilAsync(async () => await database.OutboxCountAsync() == 0, ct);
        await service.StopAsync(ct);
    }

    private async Task AdminAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(postgres.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> condition, CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        while (!await condition())
            await Task.Delay(50, deadline.Token);
    }
}
