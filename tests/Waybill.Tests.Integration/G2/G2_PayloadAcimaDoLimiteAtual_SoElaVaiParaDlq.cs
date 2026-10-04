using Npgsql;

namespace Waybill.Tests.Integration.G2;

// G2: the only message defect left after ADR 0002 is size. A row written under a larger limit than the current one
// goes to the DLQ with its reason, without touching the broker, and does not take its batch with it.
[Collection(PostgresCollection.Name)]
public sealed class G2_PayloadAcimaDoLimiteAtual_SoElaVaiParaDlq(PostgresFixture postgres)
{
    [Fact]
    public async Task G2_PayloadAcimaDoLimiteAtual_SoElaVaiParaDlq_NumLoteDeCem()
    {
        var ct = TestContext.Current.CancellationToken;
        var database = await TestDatabase.CreateAsync(postgres);
        // Written while the limit was 64 KiB; message 42 is large.
        var ids = await DispatcherHarness.EnqueueAsync(database, 100,
            i => new InvoicePaid(Guid.NewGuid(), i == 42 ? 1e20m : i), maxPayloadBytes: 64 * 1024);
        await using var dataSource = NpgsqlDataSource.Create(database.ConnectionString);
        var transport = new FakeTransport();
        var largest = (int)await database.ScalarAsync("SELECT max(length(payload)) FROM waybill.outbox");
        var smallest = (int)await database.ScalarAsync("SELECT min(length(payload)) FROM waybill.outbox");
        Assert.True(largest > smallest);

        // The limit was lowered since: only message 42 is now above it.
        var dispatcher = DispatcherHarness.Create(dataSource, transport, DispatcherHarness.Options(database, o => o.BatchSize = 100),
            maxPayloadBytes: largest - 1);
        Assert.Equal(100, (await dispatcher.RunOnceAsync(ct)).Claimed);

        Assert.Equal(99, transport.Received.Count);
        Assert.DoesNotContain(transport.Received, m => m.MessageId == ids[42]);
        Assert.Equal(99, await database.CountAsync("published"));
        Assert.Equal(1, await database.ScalarAsync(
            $"SELECT count(*) FROM waybill.outbox WHERE id = '{ids[42]}' AND status = 'dlq' AND attempts = 1 AND dlq_reason LIKE '%above MaxPayloadBytes%'"));
    }
}
