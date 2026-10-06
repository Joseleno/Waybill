using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;
using Waybill.EntityFrameworkCore.Dispatching;

namespace Waybill.Tests.Integration.Observabilidade;

/// <summary>A dispatcher wired only through the public API, with its health check registered the way an app would.</summary>
public sealed class HealthHarness : IAsyncDisposable
{
    private readonly ServiceProvider _services;

    public HealthHarness(
        TestDatabase database, ITransport transport, LogSink? logs = null,
        Action<WaybillOptions>? waybill = null, Action<WaybillDispatcherOptions>? dispatcher = null)
    {
        _services = new ServiceCollection()
            .AddLogging(logging => logging.AddProvider(logs ?? new LogSink()))
            .AddMetrics()
            .AddWaybill(o =>
            {
                o.MaxPayloadBytes = 64 * 1024;
                waybill?.Invoke(o);
            })
            .AddSingleton(transport)
            .AddWaybillDispatcher(o =>
            {
                o.ConnectionString = database.ConnectionString;
                o.PollingInterval = TimeSpan.FromMilliseconds(100);
                dispatcher?.Invoke(o);
            })
            .AddHealthChecks().AddWaybillDispatcherCheck().Services
            .BuildServiceProvider();
        Dispatcher = _services.GetServices<IHostedService>().OfType<WaybillDispatcherService>().Single();
    }

    internal WaybillDispatcherService Dispatcher { get; }

    public async Task<HealthReportEntry> CheckAsync(CancellationToken ct) =>
        (await _services.GetRequiredService<HealthCheckService>().CheckHealthAsync(ct)).Entries["waybill-dispatcher"];

    public async Task<HealthReportEntry> WaitForAsync(HealthStatus expected, CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        while (true)
        {
            var entry = await CheckAsync(deadline.Token);
            if (entry.Status == expected)
                return entry;
            await Task.Delay(50, deadline.Token);
        }
    }

    /// <summary>Makes the test database unreachable (connections refused, open ones terminated), or reachable again.</summary>
    public static async Task BlockAsync(PostgresFixture postgres, TestDatabase database, bool block)
    {
        var name = new NpgsqlConnectionStringBuilder(database.ConnectionString).Database!;
        await using var connection = new NpgsqlConnection(postgres.ConnectionString);
        await connection.OpenAsync();
        await using (var allow = new NpgsqlCommand($"ALTER DATABASE \"{name}\" ALLOW_CONNECTIONS {(block ? "false" : "true")}", connection))
            await allow.ExecuteNonQueryAsync();
        if (!block)
            return;
        await using (var terminate = new NpgsqlCommand($"SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE datname = '{name}'", connection))
            await terminate.ExecuteNonQueryAsync();
        NpgsqlConnection.ClearAllPools(); // the test's own pooled connections to that database were terminated too
    }

    public ValueTask DisposeAsync() => _services.DisposeAsync();
}
