using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using Waybill.EntityFrameworkCore.Dispatching;
using Waybill.RabbitMQ;
using Waybill.Tests.Integration;
using Waybill.Tests.Integration.G2;

namespace Waybill.Tests.Chaos.Broker;

// G2 against a real RabbitMQ behind Toxiproxy: outages, latency and dropped connections never send a message to
// the DLQ, never spend an attempt, and never lose a message (duplicates are allowed: at-least-once).
[Collection(ChaosBrokerCollection.Name)]
public sealed class ChaosBrokerTests(PostgresFixture postgres, ChaosBrokerFixture broker)
{
    [Fact]
    public Task G2_BrokerParado_NadaNaDlqEDrenaSozinho_Curto() => BrokerOutage(TimeSpan.FromSeconds(15));

    [Fact]
    [Trait("Category", "Long")]
    public Task G2_BrokerParado_NadaNaDlqEDrenaSozinho_UmaHora() => BrokerOutage(TimeSpan.FromHours(1));

    private async Task BrokerOutage(TimeSpan outage)
    {
        var ct = TestContext.Current.CancellationToken;
        var database = await TestDatabase.CreateAsync(postgres);
        var (exchange, queue) = await broker.DeclareTopologyAsync();
        var ids = await DispatcherHarness.EnqueueAsync(database, 200);
        await using var dataSource = NpgsqlDataSource.Create(database.ConnectionString);
        await using var transport = Transport(exchange);
        var options = DispatcherHarness.Options(database, o =>
        {
            o.BatchSize = 50;
            o.PollingInterval = TimeSpan.FromMilliseconds(100);
            o.PublishTimeout = TimeSpan.FromSeconds(3);
            o.LeaseMargin = TimeSpan.FromSeconds(2);
        });
        var service = Service(dataSource, transport, options);

        await broker.SetBrokerReachableAsync(false);
        try
        {
            await service.StartAsync(ct);
            await Task.Delay(outage, ct);

            // During the outage: nothing published, nothing in the DLQ, no attempt spent, and the breaker kept the
            // dispatcher from cycling the backlog through claims (each claim raises the fence by one).
            Assert.Equal(0, await database.CountAsync("published"));
            Assert.Equal(0, await database.CountAsync("dlq"));
            Assert.Equal(0, await database.ScalarAsync("SELECT coalesce(max(attempts), 0) FROM waybill.outbox"));
            var claimsDuringOutage = await database.ScalarAsync("SELECT coalesce(sum(fence), 0) FROM waybill.outbox");
            Assert.True(claimsDuringOutage < 200, $"{claimsDuringOutage} row claims during the outage: the breaker did not hold back");
        }
        finally
        {
            await broker.SetBrokerReachableAsync(true);
        }

        await WaitUntilAsync(async () => await database.CountAsync("published") == 200, TimeSpan.FromMinutes(2), ct);
        await service.StopAsync(ct);

        Assert.Equal(0, await database.CountAsync("dlq"));
        Assert.Equal(0, await database.ScalarAsync("SELECT coalesce(max(attempts), 0) FROM waybill.outbox"));
        Assert.Equal(ids.Select(id => id.ToString()).Order(), (await broker.DrainMessageIdsAsync(queue)).Distinct().Order());
    }

    // A broker that is up but too slow to confirm within PublishTimeout is back-pressure, not an outage: the batch
    // shrinks to one, the breaker never opens, nothing goes to the DLQ; when the latency goes away, everything flows
    // and the batch grows back.
    [Fact]
    public async Task G2_LatenciaAlta_BreakerFechadoLoteReduzido()
    {
        var ct = TestContext.Current.CancellationToken;
        var database = await TestDatabase.CreateAsync(postgres);
        var (exchange, queue) = await broker.DeclareTopologyAsync();
        var ids = await DispatcherHarness.EnqueueAsync(database, 41);
        await using var dataSource = NpgsqlDataSource.Create(database.ConnectionString);
        await using var transport = Transport(exchange);
        var dispatcher = DispatcherHarness.Create(dataSource, transport, DispatcherHarness.Options(database, o =>
        {
            o.BatchSize = 16;
            o.PublishTimeout = TimeSpan.FromSeconds(1);
        }));

        Assert.Equal(DispatchOutcome.Progress, (await dispatcher.RunOnceAsync(ct)).Outcome); // warm connection, 16 published
        await broker.AddLatencyAsync(1_500);
        var outcomes = new List<DispatchOutcome>();
        try
        {
            for (var cycle = 0; cycle < 6; cycle++)
                outcomes.Add((await dispatcher.RunOnceAsync(ct)).Outcome);
        }
        finally
        {
            await broker.RemoveLatencyAsync();
        }

        Assert.DoesNotContain(DispatchOutcome.ConnectionFailure, outcomes);
        Assert.DoesNotContain(DispatchOutcome.BreakerOpen, outcomes);
        Assert.Contains(DispatchOutcome.Pressure, outcomes);
        Assert.Equal(1, dispatcher.CurrentBatchSize);

        await WaitUntilAsync(async () =>
        {
            await dispatcher.RunOnceAsync(ct);
            return await database.CountAsync("published") == 41;
        }, TimeSpan.FromMinutes(1), ct);

        Assert.True(dispatcher.CurrentBatchSize > 1, "the batch did not grow back");
        Assert.Equal(0, await database.CountAsync("dlq"));
        Assert.Equal(0, await database.ScalarAsync("SELECT coalesce(max(attempts), 0) FROM waybill.outbox"));
        Assert.Equal(ids.Select(id => id.ToString()).Order(), (await broker.DrainMessageIdsAsync(queue)).Distinct().Order());
    }

    // The connection drops with large batches in flight: unconfirmed messages are published again, so nothing is
    // lost; some may arrive twice.
    [Fact]
    public async Task G2_ConexaoDerrubadaNoMeioDaPublicacao_NadaSePerde()
    {
        var ct = TestContext.Current.CancellationToken;
        var database = await TestDatabase.CreateAsync(postgres);
        var (exchange, queue) = await broker.DeclareTopologyAsync();
        var ids = await DispatcherHarness.EnqueueAsync(database, 2_000);
        await using var dataSource = NpgsqlDataSource.Create(database.ConnectionString);
        await using var transport = Transport(exchange);
        var options = DispatcherHarness.Options(database, o =>
        {
            o.BatchSize = 200;
            o.PollingInterval = TimeSpan.FromMilliseconds(100);
        });
        var service = Service(dataSource, transport, options);

        await broker.AddLatencyAsync(50); // slow enough to keep batches in flight
        try
        {
            await service.StartAsync(ct);
            await WaitUntilAsync(async () => await database.CountAsync("published") > 0, TimeSpan.FromSeconds(60), ct);
            await broker.SetBrokerReachableAsync(false); // drops the connection mid-batch
            await Task.Delay(TimeSpan.FromSeconds(3), ct);
            await broker.SetBrokerReachableAsync(true);
        }
        finally
        {
            await broker.RemoveLatencyAsync();
        }

        await WaitUntilAsync(async () => await database.CountAsync("published") == 2_000, TimeSpan.FromMinutes(2), ct);
        await service.StopAsync(ct);

        var delivered = await broker.DrainMessageIdsAsync(queue);
        TestContext.Current.SendDiagnosticMessage($"delivered {delivered.Count} for 2000 messages ({delivered.Count - delivered.Distinct().Count()} duplicates)");
        Assert.Equal(ids.Select(id => id.ToString()).Order(), delivered.Distinct().Order());
        Assert.Equal(0, await database.CountAsync("dlq"));
        Assert.Equal(0, await database.ScalarAsync("SELECT coalesce(max(attempts), 0) FROM waybill.outbox"));
    }

    private RabbitMqTransport Transport(string exchange) =>
        new(Options.Create(new WaybillRabbitMqOptions { Uri = broker.ProxiedUri, Exchange = exchange }), NullLogger<RabbitMqTransport>.Instance);

    private static WaybillDispatcherService Service(NpgsqlDataSource dataSource, RabbitMqTransport transport, WaybillDispatcherOptions options) =>
        new(DispatcherHarness.Create(dataSource, transport, options), Options.Create(options), NullLogger<WaybillDispatcherService>.Instance);

    private static async Task WaitUntilAsync(Func<Task<bool>> condition, TimeSpan timeout, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!await condition())
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException($"condition not met within {timeout}");
            await Task.Delay(200, ct);
        }
    }
}
