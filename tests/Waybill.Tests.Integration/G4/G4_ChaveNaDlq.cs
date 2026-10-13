using Microsoft.Extensions.Logging;
using Npgsql;
using Waybill.EntityFrameworkCore.Dispatching;
using Waybill.Tests.Integration.G2;

namespace Waybill.Tests.Integration.G4;

// A message in the DLQ is its key's head until it is requeued or released: its key stops, the others flow, and the
// stopped keys are counted and logged (ADR 0008).
[Collection(PostgresCollection.Name)]
public sealed class G4_ChaveNaDlq(PostgresFixture postgres)
{
    [Fact]
    public async Task G4_MensagemNaDlq_SoAquelaChaveParaEMetricaSobe()
    {
        var ct = TestContext.Current.CancellationToken;
        var logs = new LogSink();
        await using var scenario = await DlqScenario.StartAsync(postgres, logs);

        for (var i = 0; i < 4; i++)
            await scenario.Dispatcher.RunOnceAsync(ct);

        Assert.Equal("1:dlq,2:pending,3:pending", await scenario.StatusesAsync("order-42"));
        Assert.Equal("1:published,2:published,3:published", await scenario.StatusesAsync("other-1"));
        Assert.Single(scenario.Transport.Received, m => m.Key == "order-42"); // the head, once; nothing behind it
        Assert.Equal(1, await scenario.BlockedKeysAsync(ct));
        Assert.Contains(logs.Entries, e => e.Level == LogLevel.Error && e.Message.Contains("key order-42, sequence 1") && e.Message.Contains("rejected by the test"));
    }

    // With ordering off, old DLQ rows stop nothing: the gauge is not reported at all, rather than counting them.
    [Fact]
    public async Task G4_ChavesBloqueadas_SemOrdenacao_NaoReportado()
    {
        var ct = TestContext.Current.CancellationToken;
        var database = await TestDatabase.CreateAsync(postgres);
        await database.ExecuteAsync("""
            INSERT INTO waybill.outbox (id, type, key, key_hash, sequence, payload, content_type, status, dlq_reason)
            VALUES (gen_random_uuid(), 'billing.invoice-paid.v1', 'order-42', 0, 1, '\x00', 'application/json', 'dlq', 'old')
            """);
        await using var dataSource = NpgsqlDataSource.Create(database.ConnectionString);
        using var metrics = new OutboxMetrics(null);

        await new OldestPendingSampler(new OutboxStore(dataSource), metrics).SampleAsync(ct);

        Assert.Null(metrics.BlockedKeys);
    }
}

/// <summary>Ordering on; order-42 has three messages and its first is a defect the broker refuses; other-1 has three good ones.</summary>
public sealed class DlqScenario : IAsyncDisposable
{
    private int _defective = 1;

    private DlqScenario(TestDatabase database, NpgsqlDataSource dataSource, FakeTransport transport, OutboxDispatcher dispatcher)
    {
        Database = database;
        DataSource = dataSource;
        Transport = transport;
        Dispatcher = dispatcher;
    }

    public TestDatabase Database { get; }
    public NpgsqlDataSource DataSource { get; }
    public FakeTransport Transport { get; }
    internal OutboxDispatcher Dispatcher { get; }

    /// <param name="postgres">The server.</param>
    /// <param name="logs">Where the dispatcher logs.</param>
    /// <param name="returnedKey">A key whose messages come back unroutable (basic.return) and wait.</param>
    public static async Task<DlqScenario> StartAsync(PostgresFixture postgres, LogSink? logs = null, string? returnedKey = null)
    {
        var database = await TestDatabase.CreateAsync(postgres);
        var dataSource = NpgsqlDataSource.Create(database.ConnectionString);
        DlqScenario? scenario = null;
        var transport = new FakeTransport((_, batch, _) => Task.FromResult<IReadOnlyList<PublishResult>>(batch.Select(m =>
            m.Key == "order-42" && Volatile.Read(ref scenario!._defective) == 1 ? PublishResult.Defect("rejected by the test")
            : m.Key == returnedKey ? new PublishResult(PublishStatus.Returned, "312 NO_ROUTE")
            : PublishResult.Confirmed).ToList()));
        var dispatcher = OrderingHarness.Create(dataSource, transport, OrderingHarness.Options(database, partitions: 4), logs: logs);
        scenario = new DlqScenario(database, dataSource, transport, dispatcher);
        Assert.Null(await dispatcher.CheckOrderingAsync(atStartup: true, TestContext.Current.CancellationToken));
        var keys = new List<string> { "order-42", "order-42", "order-42", "other-1", "other-1", "other-1" };
        if (returnedKey is not null)
            keys.AddRange([returnedKey, returnedKey]);
        await DispatcherHarness.EnqueueAsync(database, keys.Count, key: i => keys[i]);
        return scenario;
    }

    /// <summary>From now on order-42's messages go through.</summary>
    public void Fix() => Volatile.Write(ref _defective, 0);

    /// <summary>"sequence:status" of every row of the key, in sequence order.</summary>
    public async Task<string> StatusesAsync(string key)
    {
        await using var command = DataSource.CreateCommand(
            $"SELECT string_agg(sequence || ':' || status, ',' ORDER BY sequence) FROM waybill.outbox WHERE key = '{key}'");
        return (string)(await command.ExecuteScalarAsync())!;
    }

    public async Task<long?> BlockedKeysAsync(CancellationToken ct)
    {
        using var metrics = new OutboxMetrics(null);
        await new OldestPendingSampler(new OutboxStore(DataSource), metrics).SampleAsync(ct);
        return metrics.BlockedKeys;
    }

    public ValueTask DisposeAsync() => DataSource.DisposeAsync();
}
