using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Waybill.Tests.Unit;

public sealed record InvoicePaid(Guid InvoiceId, decimal Amount);

public sealed record NotRegistered(int Value);

public sealed record Blob(string Content);

// A getter that throws stands for any serialization failure (unsupported value, converter bug).
public sealed record Unserializable(int Value)
{
    public int Broken => throw new InvalidOperationException("broken getter");
}

[JsonSerializable(typeof(InvoicePaid))]
[JsonSerializable(typeof(NotRegistered))]
[JsonSerializable(typeof(Blob))]
[JsonSerializable(typeof(Unserializable))]
internal sealed partial class TestJson : JsonSerializerContext;

public sealed class EnvelopeTests
{
    private static WaybillOptions Options(int maxPayloadBytes = 1024) =>
        new WaybillOptions { MaxPayloadBytes = maxPayloadBytes }
            .AddMessage("billing.invoice-paid.v1", TestJson.Default.InvoicePaid)
            .AddMessage("test.blob.v1", TestJson.Default.Blob)
            .AddMessage("test.unserializable.v1", TestJson.Default.Unserializable);

    [Fact]
    public void Envelope_FixaMessageIdUuidV7NoEnqueue()
    {
        var envelope = EnvelopeFactory.Create(Options(), new InvoicePaid(Guid.NewGuid(), 10m), "inv-1", null, null);

        Assert.Equal(7, envelope.Id.Version);
        Assert.Equal("billing.invoice-paid.v1", envelope.Name);
        Assert.Equal("inv-1", envelope.Key);
        Assert.Equal(KeyHash.Of("inv-1"), envelope.KeyHash);
        Assert.Equal("application/json", envelope.ContentType);
    }

    [Fact]
    public void Envelope_SerializaComOTypeInfoRegistrado()
    {
        var message = new InvoicePaid(Guid.NewGuid(), 42.5m);

        var envelope = EnvelopeFactory.Create(Options(), message, null, null, null);

        Assert.Equal(message, JsonSerializer.Deserialize(envelope.Payload, TestJson.Default.InvoicePaid));
    }

    [Fact]
    public void Envelope_TipoNaoRegistrado_FalhaNoEnqueue()
    {
        var error = Assert.Throws<InvalidOperationException>(
            () => EnvelopeFactory.Create(Options(), new NotRegistered(1), null, null, null));

        Assert.Contains("is not registered", error.Message);
    }

    // Serialization runs inside Enqueue, before the outbox row is added to the change tracker, so the failure
    // reaches whoever enqueued and nothing is written.
    [Fact]
    public void Envelope_FalhaDeSerializacao_FalhaNoEnqueue()
    {
        var error = Assert.Throws<InvalidOperationException>(
            () => EnvelopeFactory.Create(Options(), new Unserializable(1), null, null, null));

        Assert.Equal("broken getter", error.Message);
    }

    [Fact]
    public void Envelope_PayloadAcimaDoLimite_FalhaNoEnqueue()
    {
        var error = Assert.Throws<InvalidOperationException>(
            () => EnvelopeFactory.Create(Options(maxPayloadBytes: 64), new Blob(new string('x', 100)), null, null, null));

        Assert.Contains("above MaxPayloadBytes", error.Message);
    }

    [Fact]
    public void Envelope_SemMaxPayloadBytes_Falha()
    {
        var options = new WaybillOptions().AddMessage("billing.invoice-paid.v1", TestJson.Default.InvoicePaid);

        Assert.Throws<InvalidOperationException>(
            () => EnvelopeFactory.Create(options, new InvoicePaid(Guid.NewGuid(), 1m), null, null, null));
    }

    [Fact]
    public void Envelope_CapturaTraceparentECorrelacao()
    {
        using var activity = new Activity("test").SetIdFormat(ActivityIdFormat.W3C).Start();
        activity.TraceStateString = "vendor=1";

        var envelope = EnvelopeFactory.Create(Options(), new InvoicePaid(Guid.NewGuid(), 1m), null, "corr-1", "tenant-1");

        using var headers = JsonDocument.Parse(envelope.Headers!);
        Assert.Equal(activity.Id, headers.RootElement.GetProperty("traceparent").GetString());
        Assert.Equal("vendor=1", headers.RootElement.GetProperty("tracestate").GetString());
        Assert.Equal("corr-1", headers.RootElement.GetProperty("correlation_id").GetString());
        Assert.Equal("tenant-1", headers.RootElement.GetProperty("tenant_id").GetString());
    }

    [Fact]
    public void Envelope_SemTraceNemIds_SemHeaders()
    {
        Assert.Null(Activity.Current);

        var envelope = EnvelopeFactory.Create(Options(), new InvoicePaid(Guid.NewGuid(), 1m), null, null, null);

        Assert.Null(envelope.Headers);
    }

    // Name and correlation id travel as broker properties limited to 255 UTF-8 bytes (AMQP short strings): rejected
    // at registration and enqueue, never left to fail at every publish.
    [Fact]
    public void Envelope_CorrelacaoAcimaDe255Bytes_FalhaNoEnqueue()
    {
        var error = Assert.Throws<InvalidOperationException>(
            () => EnvelopeFactory.Create(Options(), new InvoicePaid(Guid.NewGuid(), 1m), null, new string('é', 128), null)); // 256 bytes

        Assert.Contains("255 UTF-8 bytes", error.Message);
        EnvelopeFactory.Create(Options(), new InvoicePaid(Guid.NewGuid(), 1m), null, new string('c', 255), null); // the limit itself is fine
    }

    [Fact]
    public void Envelope_ChaveOuTenantAcimaDe255Bytes_FalhaNoEnqueue()
    {
        Assert.Throws<InvalidOperationException>(
            () => EnvelopeFactory.Create(Options(), new InvoicePaid(Guid.NewGuid(), 1m), new string('k', 256), null, null));
        Assert.Throws<InvalidOperationException>(
            () => EnvelopeFactory.Create(Options(), new InvoicePaid(Guid.NewGuid(), 1m), null, null, new string('t', 256)));
    }

    [Fact]
    public void Registro_NomeAcimaDe255Bytes_Falha()
    {
        Assert.Throws<InvalidOperationException>(() => new WaybillOptions().AddMessage(new string('n', 256), TestJson.Default.InvoicePaid));
    }

    [Fact]
    public void Registro_NomeOuTipoDuplicado_Falha()
    {
        var options = Options();

        Assert.Throws<InvalidOperationException>(() => options.AddMessage("billing.invoice-paid.v1", TestJson.Default.NotRegistered));
        Assert.Throws<InvalidOperationException>(() => options.AddMessage("other.name", TestJson.Default.InvoicePaid));
    }
}
