using System.Collections.Concurrent;
using System.Diagnostics;
using Npgsql;
using Waybill.EntityFrameworkCore.Dispatching;
using Waybill.Tests.Integration.G2;
using Waybill.Tests.Integration.G4;
using Waybill.Tests.Integration.Retencao;

namespace Waybill.Tests.Integration.Rastro;

// Tests that listen to sources without filtering by their own trace (the dispatcher's SQL, spans that should not exist
// at all): they run alone, after the parallel collections.
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class GlobalDiagnosticsCollection : ICollectionFixture<PostgresFixture>
{
    public const string Name = "postgres-global-diagnostics";
}

[Collection(GlobalDiagnosticsCollection.Name)]
public sealed class Rastro_EstadoGlobal(PostgresFixture postgres)
{
    private sealed class Everything : IDisposable
    {
        private readonly ActivityListener _listener;

        public Everything(params string[] sources)
        {
            _listener = new ActivityListener
            {
                ShouldListenTo = source => sources.Contains(source.Name),
                Sample = (ref ActivityCreationOptions<ActivityContext> options) => ActivitySamplingResult.AllDataAndRecorded,
                ActivityStopped = Stopped.Enqueue,
            };
            ActivitySource.AddActivityListener(_listener);
        }

        public ConcurrentQueue<Activity> Stopped { get; } = new();

        public void Dispose() => _listener.Dispose();
    }

    // The dispatcher's own work (its marking, its logs) never runs under a message's span, also when the cycle inherits
    // a Current that has ended since, as a host started inside a span leaves its dispatcher.
    [Fact]
    public async Task Rastro_Ciclo_SqlForaDosSpansDeMensagem()
    {
        var ct = TestContext.Current.CancellationToken;
        using var everything = new Everything("Npgsql", WaybillDiagnostics.ActivitySourceName);
        var database = await TestDatabase.CreateAsync(postgres);
        await DispatcherHarness.EnqueueAsync(database, 3);
        await using var dataSource = NpgsqlDataSource.Create(database.ConnectionString);
        var dispatcher = DispatcherHarness.Create(dataSource, new FakeTransport(), DispatcherHarness.Options(database));
        var host = new Activity("host").Start();
        var release = new TaskCompletionSource();
        var cycle = Task.Run(async () =>
        {
            await release.Task;
            var before = Activity.Current;
            Assert.True(before is { IsStopped: true });
            await dispatcher.RunOnceAsync(ct);
            return Activity.Current == before;
        }, ct);
        host.Stop();
        release.SetResult();

        Assert.True(await cycle);
        var sends = everything.Stopped.Where(a => a.OperationName.StartsWith("send", StringComparison.Ordinal)).Select(a => a.SpanId).ToHashSet();
        var commands = everything.Stopped.Where(a => a.Source.Name == "Npgsql").ToList();
        Assert.Equal(3, sends.Count);
        Assert.NotEmpty(commands); // control: the SQL is seen
        Assert.DoesNotContain(commands, c => sends.Contains(c.ParentSpanId));
        Assert.Equal(3, await DispatcherHarness.CountAsync(database, "published"));
    }

    // A listener that throws when a send span starts costs the message its span, never its publication.
    [Fact]
    public async Task Rastro_Ciclo_OuvinteQueLanca_PublicaMesmoAssim()
    {
        var ct = TestContext.Current.CancellationToken;
        using var throwing = new ActivityListener
        {
            ShouldListenTo = source => source.Name == WaybillDiagnostics.ActivitySourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> options) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStarted = activity =>
            {
                if (activity.OperationName.StartsWith("send", StringComparison.Ordinal))
                    throw new InvalidOperationException("listener failure");
            },
        };
        ActivitySource.AddActivityListener(throwing);
        var database = await TestDatabase.CreateAsync(postgres);
        await DispatcherHarness.EnqueueAsync(database, 3);
        await using var dataSource = NpgsqlDataSource.Create(database.ConnectionString);
        var dispatcher = DispatcherHarness.Create(dataSource, new FakeTransport(), DispatcherHarness.Options(database));

        await dispatcher.RunOnceAsync(ct);

        Assert.Null(Activity.Current);
        Assert.Equal(3, await DispatcherHarness.CountAsync(database, "published"));
    }

    // Claim, partition upkeep, the repair sweep, sampling and retention open no span: they would be a trace per polling
    // cycle, unrelated to any message.
    [Fact]
    public async Task Rastro_SemSpanDeCiclo()
    {
        var ct = TestContext.Current.CancellationToken;
        using var everything = new Everything(WaybillDiagnostics.ActivitySourceName);
        var database = await TestDatabase.CreateAsync(postgres);
        await using var dataSource = NpgsqlDataSource.Create(database.ConnectionString);
        var dispatcher = OrderingHarness.Create(dataSource, new FakeTransport(), OrderingHarness.Options(database, partitions: 4));
        Assert.Null(await dispatcher.CheckOrderingAsync(atStartup: true, ct));

        for (var i = 0; i < 3; i++)
            await dispatcher.RunOnceAsync(ct);
        using (var metrics = new OutboxMetrics(null))
            await new OldestPendingSampler(new OutboxStore(dataSource), metrics).SampleAsync(ct);
        await RetentionHarness.Create(dataSource, RetentionHarness.Options(database)).RunOnceAsync(ct);

        Assert.Empty(everything.Stopped);
    }
}
