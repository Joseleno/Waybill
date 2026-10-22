using System.Diagnostics;
using System.Text.Json;

namespace Waybill.Tests.Unit;

// The create span (stage 11): opened at Enqueue, after every check, under whatever is current. The traceparent stored
// with the message is then its own, so what the message causes later hangs from it. Without a listener, or under a
// request nobody samples, nothing is opened and the stored traceparent is the caller's, as in 0.1.
public sealed class RastroCreateTests
{
    internal static WaybillOptions Options(bool keyAndTenant = false)
    {
        var options = new WaybillOptions { MaxPayloadBytes = 1024, RecordKeyAndTenantInTraces = keyAndTenant };
        return options.AddMessage("billing.invoice-paid.v1", TestJson.Default.InvoicePaid);
    }

    internal static string? TraceParent(MessageEnvelope envelope)
    {
        using var headers = JsonDocument.Parse(envelope.Headers!);
        return headers.RootElement.TryGetProperty("traceparent", out var value) ? value.GetString() : null;
    }

    [Fact]
    public void Rastro_Create_ComOuvinte_GravaOContextoDoSpan()
    {
        using var recorder = new TraceRecorder(WaybillDiagnostics.ActivitySourceName);
        using var root = recorder.StartRoot();

        var envelope = EnvelopeFactory.Create(Options(), new InvoicePaid(Guid.NewGuid(), 1m), "invoice-1", null, "tenant-1");

        var create = Assert.Single(recorder.Stopped);
        Assert.Equal(("create", ActivityKind.Producer, ActivityIdFormat.W3C), (create.OperationName, create.Kind, create.IdFormat));
        Assert.Equal(root.SpanId, create.ParentSpanId);
        Assert.Equal(create.Id, TraceParent(envelope));
        Assert.Same(root, Activity.Current);
        Assert.Equal("create", create.GetTagItem("messaging.operation.type"));
        Assert.Equal("create", create.GetTagItem("messaging.operation.name"));
        Assert.Equal(envelope.Id.ToString(), create.GetTagItem("messaging.message.id"));
        Assert.Equal("billing.invoice-paid.v1", create.GetTagItem("waybill.message.name"));
    }

    [Fact]
    public void Rastro_Create_SemOuvinte_TraceparentDaV01()
    {
        using var recorder = new TraceRecorder(); // nothing listens to Waybill's source
        using var root = recorder.StartRoot();

        var envelope = EnvelopeFactory.Create(Options(), new InvoicePaid(Guid.NewGuid(), 1m), null, null, null);

        Assert.Equal(root.Id, TraceParent(envelope));
    }

    // An application that listens to Waybill but not to its incoming requests: the request is an activity nobody
    // records, and a parent-based sampler opens nothing under it.
    [Fact]
    public void Rastro_Create_RaizNaoAmostrada_SemSpans()
    {
        using var recorder = new TraceRecorder(recorded: false, WaybillDiagnostics.ActivitySourceName);
        using var root = recorder.StartRoot();

        var envelope = EnvelopeFactory.Create(Options(), new InvoicePaid(Guid.NewGuid(), 1m), null, null, null);

        Assert.Empty(recorder.Stopped);
        Assert.Equal(root.Id, TraceParent(envelope));
    }

    public static TheoryData<string> Refused => ["unregistered", "payload", "correlation"];

    [Theory]
    [MemberData(nameof(Refused))]
    public void Rastro_Create_EnqueueRecusado_SemSpan(string refused)
    {
        using var recorder = new TraceRecorder(WaybillDiagnostics.ActivitySourceName);
        using var root = recorder.StartRoot();

        var tooSmall = new WaybillOptions { MaxPayloadBytes = 8 }.AddMessage("billing.invoice-paid.v1", TestJson.Default.InvoicePaid);

        Assert.Throws<InvalidOperationException>(() => refused switch
        {
            "unregistered" => EnvelopeFactory.Create(Options(), new NotRegistered(1), null, null, null),
            "payload" => EnvelopeFactory.Create(tooSmall, new InvoicePaid(Guid.NewGuid(), 1m), null, null, null),
            _ => EnvelopeFactory.Create(Options(), new InvoicePaid(Guid.NewGuid(), 1m), null, new string('é', 128), null),
        });

        Assert.Empty(recorder.Stopped);
        Assert.Same(root, Activity.Current);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Rastro_ChaveETenant_SoComAOpcao_NoCreate(bool recorded)
    {
        using var recorder = new TraceRecorder(WaybillDiagnostics.ActivitySourceName);
        using var root = recorder.StartRoot();

        EnvelopeFactory.Create(Options(keyAndTenant: recorded), new InvoicePaid(Guid.NewGuid(), 1m), "invoice-1", null, "tenant-1");

        var create = Assert.Single(recorder.Stopped);
        Assert.Equal(recorded ? "invoice-1" : null, create.GetTagItem("waybill.key"));
        Assert.Equal(recorded ? "tenant-1" : null, create.GetTagItem("waybill.tenant_id"));
    }
}

// Spans opened outside the caller's flow (the dispatcher's send spans) leave the caller's Activity.Current as it was.
public sealed class RastroIsolatedTests
{
    // The dispatcher inherits the execution context of whoever started the host, whose activity may have stopped since:
    // the setter ignores a stopped activity, so restoring Current by hand would leave the new span current.
    [Fact]
    public async Task Rastro_StartIsolated_CurrentParado_NaoMuda()
    {
        using var recorder = new TraceRecorder(WaybillDiagnostics.ActivitySourceName);
        var host = new Activity("host").Start();
        var release = new TaskCompletionSource();
        var cycle = Task.Run(async () =>
        {
            await release.Task;
            Assert.True(Activity.Current is { IsStopped: true });
            var before = Activity.Current;

            var span = WaybillActivity.StartIsolated("send", ActivityKind.Producer, recorder.Root.Context);
            Assert.Same(before, Activity.Current);
            WaybillActivity.StopIsolated(span);
            Assert.Same(before, Activity.Current);
            return span;
        }, TestContext.Current.CancellationToken);
        recorder.StartRoot().Stop(); // a recorded context to parent the span on
        host.Stop();
        release.SetResult();

        Assert.NotNull(await cycle);
    }

    [Fact]
    public void Rastro_StartIsolated_OuvinteQueLanca_CurrentIntacto()
    {
        using var recorder = new TraceRecorder(WaybillDiagnostics.ActivitySourceName);
        using var root = recorder.StartRoot();
        using var throwing = new ActivityListener
        {
            ShouldListenTo = source => source.Name == WaybillDiagnostics.ActivitySourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> options) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStarted = activity =>
            {
                if (activity.TraceId == recorder.TraceId)
                    throw new InvalidOperationException("listener failure");
            },
        };
        ActivitySource.AddActivityListener(throwing);

        Assert.Throws<InvalidOperationException>(() => WaybillActivity.StartIsolated("send", ActivityKind.Producer, root.Context));

        Assert.Same(root, Activity.Current);
    }

    [Fact]
    public void Rastro_StartIsolated_PaiDefault_RaizMesmoSobAmbiente()
    {
        using var recorder = new TraceRecorder(WaybillDiagnostics.ActivitySourceName);
        recorder.OwnRootName("send-root-test");
        using var ambient = recorder.StartRoot();

        var span = WaybillActivity.StartIsolated("send-root-test", ActivityKind.Producer, default);
        WaybillActivity.StopIsolated(span);

        Assert.NotNull(span);
        Assert.Equal(default, span.ParentSpanId);
        Assert.NotEqual(ambient.TraceId, span.TraceId);
        Assert.Same(ambient, Activity.Current);
    }
}

// Activity.DefaultIdFormat and ForceDefaultIdFormat are process-wide: these tests run alone.
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class DiagnosticsGlobalState
{
    public const string Name = "diagnostics-global-state";
}

[Collection(DiagnosticsGlobalState.Name)]
public sealed class RastroIdFormatTests
{
    // Under a process-wide hierarchical default the create span stays W3C, so the stored traceparent is still one a
    // consumer can read.
    [Fact]
    public void Rastro_Create_IdHierarquico_ContinuaW3C()
    {
        var (format, force) = (Activity.DefaultIdFormat, Activity.ForceDefaultIdFormat);
        var created = new List<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == WaybillDiagnostics.ActivitySourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> options) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = created.Add,
        };
        ActivitySource.AddActivityListener(listener);
        try
        {
            (Activity.DefaultIdFormat, Activity.ForceDefaultIdFormat) = (ActivityIdFormat.Hierarchical, true);

            var envelope = EnvelopeFactory.Create(RastroCreateTests.Options(), new InvoicePaid(Guid.NewGuid(), 1m), null, null, null);

            var create = Assert.Single(created, a => a.GetTagItem("messaging.message.id") as string == envelope.Id.ToString());
            Assert.Equal(ActivityIdFormat.W3C, create.IdFormat);
            Assert.Equal(create.Id, RastroCreateTests.TraceParent(envelope));
        }
        finally
        {
            (Activity.DefaultIdFormat, Activity.ForceDefaultIdFormat) = (format, force);
        }
    }
}
