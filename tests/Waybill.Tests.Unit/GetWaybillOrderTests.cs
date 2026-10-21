using System.Text;
using RabbitMQ.Client;
using Waybill.RabbitMQ;

namespace Waybill.Tests.Unit;

// The consumer reads an ordered message's key and sequence back from the waybill-key and waybill-sequence headers
// (stage 10). From the broker, header strings arrive as byte[] (measured on RabbitMQ 4.3.6); properties built in a
// test carry strings. Reading only strings would treat every delivered message as unordered, silently.
public sealed class GetWaybillOrderTests
{
    private static BasicProperties With(params (string Name, object? Value)[] headers) =>
        new() { Headers = headers.ToDictionary(h => h.Name, h => h.Value) };

    private static byte[] Bytes(string text) => Encoding.UTF8.GetBytes(text);

    [Fact]
    public void GetWaybillOrder_CabecalhosEmBytes_LeChaveESequencia()
    {
        Assert.Equal(new MessageOrder("order-42", 7), With(("waybill-key", Bytes("order-42")), ("waybill-sequence", Bytes("7"))).GetWaybillOrder());
    }

    [Fact]
    public void GetWaybillOrder_CabecalhosEmString_LeChaveESequencia()
    {
        Assert.Equal(new MessageOrder("order-42", 7), With(("waybill-key", "order-42"), ("waybill-sequence", "7")).GetWaybillOrder());
    }

    [Fact]
    public void GetWaybillOrder_SemSequencia_Nulo()
    {
        Assert.Null(new BasicProperties().GetWaybillOrder());
        Assert.Null(With(("waybill-key", Bytes("order-42"))).GetWaybillOrder()); // keyed, not ordered (ordering off)
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("+3")]
    [InlineData(" 3")]
    [InlineData("3.0")]
    [InlineData("seven")]
    [InlineData("99999999999999999999")]
    public void GetWaybillOrder_SequenciaMalformada_Erro(string sequence)
    {
        var error = Assert.Throws<InvalidOperationException>(() => With(("waybill-key", Bytes("order-42")), ("waybill-sequence", Bytes(sequence))).GetWaybillOrder());
        Assert.Contains("waybill-sequence", error.Message);
    }

    [Fact]
    public void GetWaybillOrder_SequenciaSemChave_Erro()
    {
        var error = Assert.Throws<InvalidOperationException>(() => With(("waybill-sequence", Bytes("3"))).GetWaybillOrder());
        Assert.Contains("waybill-key", error.Message);
    }

    [Fact]
    public void GetWaybillOrder_ChaveAcimaDe255Bytes_Erro()
    {
        Assert.Throws<InvalidOperationException>(() => With(("waybill-key", Bytes(new string('k', 256))), ("waybill-sequence", Bytes("3"))).GetWaybillOrder());
    }

    [Fact]
    public void GetWaybillOrder_TipoDeCabecalhoInesperado_Erro()
    {
        Assert.Throws<InvalidOperationException>(() => With(("waybill-key", Bytes("order-42")), ("waybill-sequence", 3L)).GetWaybillOrder());
    }

    // What the transport writes, the consumer reads back, an empty key included: Enqueue accepts one.
    [Theory]
    [InlineData("invoice-7")]
    [InlineData("")]
    public void GetWaybillOrder_IdaEVoltaPeloTransporte(string key)
    {
        var properties = RabbitMqTransport.Properties(new OutgoingMessage
        {
            MessageId = Guid.CreateVersion7(),
            Name = "billing.invoice-paid.v1",
            Key = key,
            Sequence = 12,
            Payload = Encoding.UTF8.GetBytes("{}"),
            ContentType = "application/json",
            Headers = new Dictionary<string, string>(),
            CreatedAt = DateTimeOffset.UtcNow,
        });
        var delivered = new BasicProperties(properties)
        {
            Headers = properties.Headers!.ToDictionary(h => h.Key, h => h.Value is string s ? (object?)Encoding.UTF8.GetBytes(s) : h.Value),
        };

        Assert.Equal(new MessageOrder(key, 12), delivered.GetWaybillOrder());
    }
}
