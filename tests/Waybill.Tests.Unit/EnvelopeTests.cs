using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Waybill.Tests.Unit;

public sealed record InvoicePaid(Guid InvoiceId, decimal Amount);

public sealed record NotRegistered(int Value);

public sealed record Blob(string Content);

[JsonSerializable(typeof(InvoicePaid))]
[JsonSerializable(typeof(NotRegistered))]
[JsonSerializable(typeof(Blob))]
internal sealed partial class TestJson : JsonSerializerContext;

public sealed class EnvelopeTests
{
    private static WaybillOptions Options(int maxPayloadBytes = 1024) =>
        new WaybillOptions { MaxPayloadBytes = maxPayloadBytes }
            .AddMessage("billing.invoice-paid.v1", TestJson.Default.InvoicePaid)
            .AddMessage("test.blob.v1", TestJson.Default.Blob);

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

    [Fact]
    public void Registro_NomeOuTipoDuplicado_Falha()
    {
        var options = Options();

        Assert.Throws<InvalidOperationException>(() => options.AddMessage("billing.invoice-paid.v1", TestJson.Default.NotRegistered));
        Assert.Throws<InvalidOperationException>(() => options.AddMessage("other.name", TestJson.Default.InvoicePaid));
    }
}
