using Waybill.Tests.Integration.G2;

namespace Waybill.Tests.Integration.Observabilidade;

// Stage 12 friction (FRICCAO.md, item 6): nothing in the log said ordering by key was on. The startup line (EventId 20)
// now says it, with P and M when it is on, also in its Ordering field.
[Collection(PostgresCollection.Name)]
public sealed class Dispatcher_LogDePartida(PostgresFixture postgres)
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Dispatcher_LogDePartida_DizAOrdenacao(bool ordered)
    {
        var ct = TestContext.Current.CancellationToken;
        var database = await TestDatabase.CreateAsync(postgres);
        var logs = new LogSink();
        await using var harness = new HealthHarness(database, new FakeTransport(), logs, dispatcher: o =>
        {
            o.OrderByKey = ordered;
            o.Partitions = 4;
            o.MaxPerKey = ordered ? 2 : 1;
        });

        await harness.Dispatcher.StartAsync(ct);
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!logs.Records.Any(r => r.EventId == 20) && DateTime.UtcNow < deadline)
            await Task.Delay(50, ct);
        await harness.Dispatcher.StopAsync(ct);

        var started = Assert.Single(logs.Records, r => r.EventId == 20);
        var ordering = ordered ? "on (Partitions = 4, MaxPerKey = 2)" : "off";
        Assert.EndsWith($"started; ordering by key is {ordering}.", started.Message);
        Assert.Equal(ordering, started.Fields["Ordering"]);
    }
}
