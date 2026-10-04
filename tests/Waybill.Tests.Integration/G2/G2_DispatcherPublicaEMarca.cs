using System.Text.Json;
using Npgsql;

namespace Waybill.Tests.Integration.G2;

[Collection(PostgresCollection.Name)]
public sealed class G2_DispatcherPublicaEMarca(PostgresFixture postgres)
{
    [Fact]
    public async Task G2_DispatcherPublicaEMarca_EmOrdemDeIdComEnvelopeCompleto()
    {
        var ct = TestContext.Current.CancellationToken;
        var database = await TestDatabase.CreateAsync(postgres);
        var ids = await DispatcherHarness.EnqueueAsync(database, 5);
        await using var dataSource = NpgsqlDataSource.Create(database.ConnectionString);
        var transport = new FakeTransport();
        var dispatcher = DispatcherHarness.Create(dataSource, transport, DispatcherHarness.Options(database));

        Assert.Equal(5, await dispatcher.RunOnceAsync(ct));
        Assert.Equal(0, await dispatcher.RunOnceAsync(ct));

        var received = transport.Received.ToList();
        Assert.Equal(ids.Order(), received.Select(m => m.MessageId));
        var first = received[0];
        Assert.Equal("billing.invoice-paid.v1", first.Name);
        Assert.Equal("invoice-0", first.Key);
        Assert.Equal("application/json", first.ContentType);
        Assert.Equal("corr-0", first.Headers["correlation_id"]);
        Assert.Equal(0m, JsonSerializer.Deserialize(first.Payload.Span, TestJson.Default.InvoicePaid)!.Amount);

        Assert.Equal(5, await database.CountAsync("published"));
        Assert.Equal(5, await database.ScalarAsync(
            "SELECT count(*) FROM waybill.outbox WHERE fence = 1 AND attempts = 0 AND lease_until IS NULL AND published_at IS NOT NULL"));
    }
}
