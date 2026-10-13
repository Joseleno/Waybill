using System.Text;
using Npgsql;
using Waybill.Tests.Integration.G2;
using Waybill.Tests.Integration.G2.RabbitMq;

namespace Waybill.Tests.Integration.G4;

// The transport receives each ordered message's sequence, and the RabbitMQ transport hands it to consumers in the
// waybill-sequence header (ADR 0008). Messages that are not ordered carry none.
public sealed class G4_SequenceNoEnvelope
{
    [Collection(PostgresCollection.Name)]
    public sealed class NoTransporte(PostgresFixture postgres)
    {
        [Fact]
        public async Task G4_SequenceNoEnvelope_OTransporteRecebeASequenceDoBanco()
        {
            var ct = TestContext.Current.CancellationToken;
            var database = await TestDatabase.CreateAsync(postgres);
            await using var dataSource = NpgsqlDataSource.Create(database.ConnectionString);
            var transport = new FakeTransport();
            var dispatcher = OrderingHarness.Create(dataSource, transport, OrderingHarness.Options(database, partitions: 4));
            Assert.Null(await dispatcher.CheckOrderingAsync(atStartup: true, ct));
            await DispatcherHarness.EnqueueAsync(database, 4, key: i => i < 3 ? "order-42" : null);

            for (var i = 0; i < 3; i++)
                await dispatcher.RunOnceAsync(ct);

            Assert.Equal(4, transport.Received.Count);
            foreach (var message in transport.Received)
            {
                var stored = await database.ScalarAsync($"SELECT coalesce(sequence, -1) FROM waybill.outbox WHERE id = '{message.MessageId}'");
                Assert.Equal(stored == -1 ? null : stored, message.Sequence);
            }
            Assert.Equal([1L, 2L, 3L], transport.Received.Where(m => m.Key is not null).Select(m => m.Sequence!.Value));
        }
    }

    [Collection(BrokerCollection.Name)]
    public sealed class NoRabbitMq(PostgresFixture postgres, RabbitMqFixture rabbit)
    {
        [Fact]
        public async Task G4_SequenceNoEnvelope_ConsumidorRecebeOHeaderWaybillSequence()
        {
            var ct = TestContext.Current.CancellationToken;
            var database = await TestDatabase.CreateAsync(postgres);
            var (exchange, queue) = await rabbit.DeclareTopologyAsync("billing.#");
            await using var dataSource = NpgsqlDataSource.Create(database.ConnectionString);
            await using var transport = G2_RabbitMq_PublicaComConfirmacaoEPropriedades.Transport(rabbit, exchange);
            var dispatcher = OrderingHarness.Create(dataSource, transport, OrderingHarness.Options(database, partitions: 4));
            Assert.Null(await dispatcher.CheckOrderingAsync(atStartup: true, ct));
            await DispatcherHarness.EnqueueAsync(database, 2, key: _ => "order-42");

            await dispatcher.RunOnceAsync(ct);
            await dispatcher.RunOnceAsync(ct);

            var delivered = await rabbit.DrainAsync(queue);
            Assert.Equal(["1", "2"], delivered.Select(m => Encoding.UTF8.GetString((byte[])m.BasicProperties.Headers!["waybill-sequence"]!)));
            Assert.All(delivered, m => Assert.Equal("order-42", Encoding.UTF8.GetString((byte[])m.BasicProperties.Headers!["waybill-key"]!)));
        }
    }
}
