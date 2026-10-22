using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Waybill.EntityFrameworkCore;
using Waybill.Tests.Integration.G4;

namespace Waybill.Tests.Integration.Rastro;

// The inbox's process span (stage 11): one per call, Internal, child of whatever the application made current (the
// client's deliver span, with RabbitMQ.Client's instrumentation), with the result as an attribute; a regression or an
// exception ends it as an error. Each run of the execution strategy is an event on it.
[Collection(PostgresCollection.Name)]
public sealed class Rastro_Process(PostgresFixture postgres)
{
    private static Activity LastProcess(TraceRecorder recorder) => recorder.Stopped.Last(a => a.OperationName == "process");

    public static TheoryData<string> Results => ["processed", "duplicate", "regressed"];

    [Theory]
    [MemberData(nameof(Results))]
    public async Task Rastro_Process_Resultados(string expected)
    {
        using var recorder = new TraceRecorder(WaybillDiagnostics.ActivitySourceName);
        var database = await TestDatabase.CreateAsync(postgres);
        await using var services = InboxOrderHarness.Services(database, out _);
        var first = Guid.CreateVersion7();
        using var root = recorder.StartRoot();
        await InboxOrderHarness.DeliverAsync(services, first, 2);

        var result = expected switch
        {
            "processed" => await InboxOrderHarness.DeliverAsync(services, Guid.CreateVersion7(), 3),
            "duplicate" => await InboxOrderHarness.DeliverAsync(services, first, 2),
            _ => await InboxOrderHarness.DeliverAsync(services, Guid.CreateVersion7(), 1),
        };

        var process = LastProcess(recorder);
        Assert.Equal(expected, result.ToString().ToLowerInvariant());
        Assert.Equal((ActivityKind.Internal, root.SpanId), (process.Kind, process.ParentSpanId));
        Assert.Equal(expected, process.GetTagItem("waybill.inbox.result"));
        Assert.Equal(InboxOrderHarness.Handler, process.GetTagItem("waybill.inbox.handler"));
        Assert.Equal("process", process.GetTagItem("messaging.operation.type"));
        Assert.Equal(expected == "regressed" ? "regressed" : null, process.GetTagItem("error.type"));
        Assert.Equal(expected == "regressed" ? ActivityStatusCode.Error : ActivityStatusCode.Unset, process.Status);
        Assert.Same(root, Activity.Current);
    }

    [Fact]
    public async Task Rastro_Process_HandlerLanca_Erro()
    {
        var ct = TestContext.Current.CancellationToken;
        using var recorder = new TraceRecorder(WaybillDiagnostics.ActivitySourceName);
        var database = await TestDatabase.CreateAsync(postgres);
        await using var services = InboxOrderHarness.Services(database, out _);
        using var root = recorder.StartRoot();
        await using var scope = services.CreateAsyncScope();
        var inbox = scope.ServiceProvider.GetRequiredService<IInbox<AppDbContext>>();

        await Assert.ThrowsAsync<InvalidOperationException>(() => inbox.ProcessAsync(
            InboxOrderHarness.Handler, Guid.CreateVersion7(), (_, _) => throw new InvalidOperationException("handler fails"), ct));

        var process = LastProcess(recorder);
        Assert.Equal(typeof(InvalidOperationException).FullName, process.GetTagItem("error.type"));
        Assert.Equal(ActivityStatusCode.Error, process.Status);
    }

    // The acknowledgement of the first commit is lost and the strategy runs the unit again: one span, two attempts, and
    // the result the caller gets (Duplicate), although this call is the one that applied the effect.
    [Fact]
    public async Task Rastro_Process_CommitAmbiguo_UmSpanComTentativas()
    {
        using var recorder = new TraceRecorder(WaybillDiagnostics.ActivitySourceName);
        var database = await TestDatabase.CreateAsync(postgres);
        var lostCommitAck = new LoseFirstCommitAcknowledgement();
        await using var services = InboxOrderHarness.Services(database, out _, o => o.EnableRetryOnFailure(3, TimeSpan.FromMilliseconds(10), null), lostCommitAck);
        using var root = recorder.StartRoot();

        await InboxOrderHarness.DeliverAsync(services, Guid.CreateVersion7(), 1);

        Assert.True(lostCommitAck.Fired);
        var process = Assert.Single(recorder.Stopped, a => a.OperationName == "process");
        Assert.Equal([1, 2], process.Events.Where(e => e.Name == "waybill.attempt").Select(e => (int)e.Tags.Single(t => t.Key == "waybill.attempt").Value!));
        Assert.Equal("duplicate", process.GetTagItem("waybill.inbox.result"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Rastro_ChaveETenant_SoComAOpcao_NoProcess(bool recorded)
    {
        using var recorder = new TraceRecorder(WaybillDiagnostics.ActivitySourceName);
        var database = await TestDatabase.CreateAsync(postgres);
        await using var services = database.Services(o => o.RecordKeyAndTenantInTraces = recorded);
        using var root = recorder.StartRoot();

        await InboxOrderHarness.DeliverAsync(services, Guid.CreateVersion7(), 1);

        var process = LastProcess(recorder);
        Assert.Equal(recorded ? InboxOrderHarness.Key : null, process.GetTagItem("waybill.key"));
        Assert.Null(process.GetTagItem("waybill.tenant_id")); // the inbox does not know the tenant
    }
}
