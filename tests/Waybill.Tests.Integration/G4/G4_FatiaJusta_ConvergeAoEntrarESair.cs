using Npgsql;
using Waybill.EntityFrameworkCore.Dispatching;

namespace Waybill.Tests.Integration.G4;

// The partitions spread over the live dispatchers, at most ceil(P / N) each, all of them held. One that leaves with a
// graceful shutdown hands its share over at the others' next cycle; one that dies hands it over when its partition
// lease runs out, and its instance row is collected later (ADR 0007).
[Collection(PostgresCollection.Name)]
public sealed class G4_FatiaJusta_ConvergeAoEntrarESair(PostgresFixture postgres)
{
    private static readonly OrderingSettings Settings = new(16, TimeSpan.FromMilliseconds(400));

    [Fact]
    public async Task G4_FatiaJusta_ConvergeAoEntrarESair_ShutdownNaHoraMortaNoLease()
    {
        var ct = TestContext.Current.CancellationToken;
        var database = await TestDatabase.CreateAsync(postgres);
        await using var dataSource = NpgsqlDataSource.Create(database.ConnectionString);
        var partitions = new PartitionStore(dataSource);
        await partitions.EnsureSettingsAsync(Settings, ct);

        var held = new Dictionary<string, SortedDictionary<int, long>>();
        async Task RoundAsync(params string[] owners)
        {
            foreach (var owner in owners)
                held[owner] = await partitions.MaintainAsync(owner, Settings, ct);
        }

        Assert.Equal(16, (await partitions.MaintainAsync("a", Settings, ct)).Count); // alone, it takes all
        for (var i = 0; i < 3; i++)
            await RoundAsync("a", "b", "c");
        Assert.All(held.Values, h => Assert.InRange(h.Count, 1, 6));
        Assert.Equal(16, held.Values.SelectMany(h => h.Keys).Distinct().Count());
        Assert.Equal(16, held.Values.Sum(h => h.Count));

        await partitions.LeaveAsync("c", ct); // graceful shutdown
        held.Remove("c");
        await RoundAsync("a", "b", "a", "b");
        Assert.Equal(8, held["a"].Count);
        Assert.Equal(8, held["b"].Count);

        // b dies: no more heartbeats. Its partitions come back after the partition lease, not before.
        await RoundAsync("a");
        Assert.Equal(8, held["a"].Count);
        await Task.Delay(Settings.PartitionLease + TimeSpan.FromMilliseconds(200), ct);
        await RoundAsync("a");
        Assert.Equal(16, held["a"].Count);

        // Its instance row goes after ten partition leases.
        Assert.Equal(1, await database.ScalarAsync("SELECT count(*) FROM waybill.outbox_instances WHERE owner = 'b'"));
        await Task.Delay(Settings.PartitionLease * 10, ct);
        await RoundAsync("a");
        Assert.Equal(0, await database.ScalarAsync("SELECT count(*) FROM waybill.outbox_instances WHERE owner = 'b'"));
    }
}
