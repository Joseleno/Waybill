using System.Diagnostics.Metrics;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Waybill.EntityFrameworkCore.Dispatching;
using Waybill.Tests.Integration.G4;

namespace Waybill.Tests.Integration.Observabilidade;

// waybill.outbox.dlq counts the messages in the outbox DLQ, with ordering on or off; waybill.outbox.blocked_keys counts
// the keys they stop while ordering is on. Both go back to 0 once the SQL of OPERATIONS.md handles the messages. Before
// the first sample neither reports anything: a 0 after a restart during an outage would read as an empty DLQ.
[Collection(PostgresCollection.Name)]
public sealed class Metrica_DlqEChavesBloqueadas(PostgresFixture postgres)
{
    [Fact]
    public async Task Metrica_DlqEChavesBloqueadas_SobemEVoltamAZero()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var scenario = await DlqScenario.StartAsync(postgres);
        await using var services = new ServiceCollection().AddMetrics().BuildServiceProvider();
        var factory = services.GetRequiredService<IMeterFactory>();
        using var metrics = new OutboxMetrics(factory);
        var sampler = new OldestPendingSampler(new OutboxStore(scenario.DataSource), metrics);
        Assert.Null(GaugeReader.Read(factory, GaugeReader.Dlq).Value);

        await scenario.Dispatcher.RunOnceAsync(ct); // order-42's first message is a defect
        await sampler.SampleAsync(ct);

        Assert.Equal((1d, "{message}"), GaugeReader.Read(factory, GaugeReader.Dlq));
        Assert.Equal(1d, GaugeReader.Read(factory, GaugeReader.BlockedKeys).Value);

        await using (var release = scenario.DataSource.CreateCommand(OperationsDoc.Sql("key-release")))
            await release.ExecuteNonQueryAsync(ct);
        await sampler.SampleAsync(ct);

        Assert.Equal(0d, GaugeReader.Read(factory, GaugeReader.Dlq).Value);
        Assert.Equal(0d, GaugeReader.Read(factory, GaugeReader.BlockedKeys).Value);
    }

    // A message without a key stops nothing: only the DLQ gauge moves, with ordering off too.
    [Fact]
    public async Task Metrica_DlqSemChave_SoODlq_TambemComOrdenacaoDesligada()
    {
        var ct = TestContext.Current.CancellationToken;
        var database = await TestDatabase.CreateAsync(postgres);
        await database.ExecuteAsync("""
            INSERT INTO waybill.outbox (id, type, key_hash, payload, content_type, status, dlq_reason)
            VALUES (gen_random_uuid(), 'billing.invoice-paid.v1', 0, '\x00', 'application/json', 'dlq', '312 NO_ROUTE')
            """);
        await using var services = new ServiceCollection().AddMetrics().BuildServiceProvider();
        var factory = services.GetRequiredService<IMeterFactory>();
        await using var dataSource = NpgsqlDataSource.Create(database.ConnectionString);
        using var metrics = new OutboxMetrics(factory);
        var sampler = new OldestPendingSampler(new OutboxStore(dataSource), metrics);

        await sampler.SampleAsync(ct);

        Assert.Equal(1d, GaugeReader.Read(factory, GaugeReader.Dlq).Value);
        Assert.Null(GaugeReader.Read(factory, GaugeReader.BlockedKeys).Value); // ordering off: not reported

        await database.ExecuteAsync(OperationsDoc.Sql("dlq-requeue"));
        await sampler.SampleAsync(ct);

        Assert.Equal(0d, GaugeReader.Read(factory, GaugeReader.Dlq).Value);
    }
}
