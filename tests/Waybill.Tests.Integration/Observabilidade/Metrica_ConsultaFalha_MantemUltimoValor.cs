using System.Diagnostics.Metrics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;
using Waybill.EntityFrameworkCore.Dispatching;
using Waybill.Tests.Integration.G2;

namespace Waybill.Tests.Integration.Observabilidade;

// AddWaybillDispatcher samples the gauge in the background. A sample that fails (database unreachable) is logged and
// leaves the last value in place, rather than reporting 0 as if the outbox were empty; sampling resumes when the
// database comes back.
[Collection(PostgresCollection.Name)]
public sealed class Metrica_ConsultaFalha_MantemUltimoValor(PostgresFixture postgres)
{
    [Fact]
    public async Task Metrica_BancoFora_LogaEMantemOUltimoValor_VoltaAAmostrarDepois()
    {
        var ct = TestContext.Current.CancellationToken;
        var database = await TestDatabase.CreateAsync(postgres);
        await DispatcherHarness.EnqueueAsync(database, 3);
        var name = new NpgsqlConnectionStringBuilder(database.ConnectionString).Database!;
        var logs = new LogSink();
        await using var services = new ServiceCollection()
            .AddLogging(logging => logging.AddProvider(logs))
            .AddMetrics()
            .AddWaybill(o => o.MaxPayloadBytes = 1024)
            .AddSingleton<ITransport>(new FakeTransport())
            .AddWaybillDispatcher(o =>
            {
                o.ConnectionString = database.ConnectionString;
                o.MetricsInterval = TimeSpan.FromMilliseconds(100);
            })
            .BuildServiceProvider();
        var factory = services.GetRequiredService<IMeterFactory>();
        var sampling = Assert.Single(services.GetServices<IHostedService>().OfType<WaybillMetricsService>());

        await sampling.StartAsync(ct);
        await WaitUntilAsync(() => GaugeReader.Read(factory).Value > 0, ct);

        await AdminAsync($"ALTER DATABASE \"{name}\" ALLOW_CONNECTIONS false");
        await AdminAsync($"SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE datname = '{name}'");
        NpgsqlConnection.ClearAllPools();
        var errorsBefore = Errors(logs);
        await WaitUntilAsync(() => Errors(logs) > errorsBefore, ct);
        var kept = GaugeReader.Read(factory).Value;
        await WaitUntilAsync(() => Errors(logs) > errorsBefore + 2, ct); // a few more failed samples

        Assert.True(kept > 0, $"kept {kept}");
        Assert.Equal(kept, GaugeReader.Read(factory).Value);

        await AdminAsync($"ALTER DATABASE \"{name}\" ALLOW_CONNECTIONS true");
        await WaitUntilAsync(() => GaugeReader.Read(factory).Value > kept, ct);
        await sampling.StopAsync(ct);
    }

    private static int Errors(LogSink logs) =>
        logs.Entries.Count(e => e.Level == LogLevel.Error && e.Category.Contains(nameof(WaybillMetricsService)));

    private async Task AdminAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(postgres.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task WaitUntilAsync(Func<bool> condition, CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        while (!condition())
            await Task.Delay(50, deadline.Token);
    }
}
