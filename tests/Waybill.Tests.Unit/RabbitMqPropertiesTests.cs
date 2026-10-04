using System.Text;
using RabbitMQ.Client;
using Waybill.RabbitMQ;

namespace Waybill.Tests.Unit;

public sealed class RabbitMqPropertiesTests
{
    private static OutgoingMessage Message(string name = "billing.invoice-paid.v1", Dictionary<string, string>? headers = null) => new()
    {
        MessageId = Guid.Parse("01920000-0000-7000-8000-000000000001"),
        Name = name,
        Key = "invoice-7",
        Payload = Encoding.UTF8.GetBytes("{}"),
        ContentType = "application/json",
        Headers = headers ?? new Dictionary<string, string>
        {
            ["traceparent"] = "00-0af7651916cd43dd8448eb211c80319c-b7ad6b7169203331-01",
            ["tracestate"] = "vendor=1",
            ["correlation_id"] = "corr-7",
            ["tenant_id"] = "tenant-7",
        },
        CreatedAt = DateTimeOffset.FromUnixTimeSeconds(1_790_000_000),
    };

    [Fact]
    public void Propriedades_MapeiamEnvelopeParaAmqp()
    {
        var properties = RabbitMqTransport.Properties(Message());

        Assert.Equal("01920000-0000-7000-8000-000000000001", properties.MessageId);
        Assert.Equal("billing.invoice-paid.v1", properties.Type);
        Assert.Equal("application/json", properties.ContentType);
        Assert.Equal(DeliveryModes.Persistent, properties.DeliveryMode);
        Assert.Equal(1_790_000_000, properties.Timestamp.UnixTime);
        Assert.Equal("corr-7", properties.CorrelationId);
        Assert.Equal("00-0af7651916cd43dd8448eb211c80319c-b7ad6b7169203331-01", properties.Headers!["traceparent"]);
        Assert.Equal("vendor=1", properties.Headers["tracestate"]);
        Assert.Equal("tenant-7", properties.Headers["tenant_id"]);
        Assert.Equal("invoice-7", properties.Headers["waybill-key"]);
        Assert.False(properties.Headers.ContainsKey("correlation_id")); // carried as the AMQP property, not twice
    }

    [Fact]
    public void Propriedades_SemHeadersOpcionais_SemCorrelacao()
    {
        var properties = RabbitMqTransport.Properties(Message(headers: []));

        Assert.Null(properties.CorrelationId);
        Assert.Equal(["waybill-key"], properties.Headers!.Keys);
    }

    [Fact]
    public void Propriedades_ShortStringAcimaDe255Bytes_Lanca()
    {
        Assert.Throws<InvalidOperationException>(() => RabbitMqTransport.Properties(Message(name: new string('n', 256))));
        Assert.Throws<InvalidOperationException>(() => RabbitMqTransport.Properties(
            Message(headers: new Dictionary<string, string> { ["correlation_id"] = new('c', 256) })));
    }
}
