using System.Reflection;
using Npgsql;
using Waybill.EntityFrameworkCore.Dispatching;
using Waybill.Tests.Integration.G2;

namespace Waybill.Tests.Integration.G4;

// The test clock (TestClock) and what the claim's correctness rests on: one instant per statement, read after the
// statement's snapshot and before any lock.
[Collection(PostgresCollection.Name)]
public sealed class G4_RelogioDeTeste(PostgresFixture postgres)
{
    // A claim paused at its clock already has its snapshot: a row committed meanwhile is not seen by it.
    [Fact]
    public async Task RelogioDeTeste_PausaOClaimDepoisDoSnapshot()
    {
        var ct = TestContext.Current.CancellationToken;
        var database = await TestDatabase.CreateAsync(postgres);
        var before = await DispatcherHarness.EnqueueAsync(database, 1, key: _ => null);
        await using var clock = await TestClock.InstallAsync(database);
        var store = new OutboxStore(clock.DataSource);

        await clock.PauseAsync();
        var claim = store.ClaimAsync("owner", 10, TimeSpan.FromSeconds(30), ct);
        await clock.WaitForPausedAsync();
        var after = await DispatcherHarness.EnqueueAsync(database, 1, key: _ => null);
        await clock.ResumeAsync();

        Assert.Equal(before, (await claim).Select(c => c.Message.MessageId));
        Assert.Equal(1, await database.ScalarAsync($"SELECT count(*) FROM waybill.outbox WHERE id = '{after[0]}' AND status = 'pending'"));
    }

    // The ordered claim reads the clock once: the partition it checks is held past the lease it takes, measured from the
    // same instant (ADR 0007), and every row is claimable at that one instant (ADR 0008).
    [Fact]
    public async Task G4_PosseNoLimite_UmInstantePorInstrucao()
    {
        var ct = TestContext.Current.CancellationToken;
        var database = await TestDatabase.CreateAsync(postgres);
        await using var dataSource = NpgsqlDataSource.Create(database.ConnectionString);
        var dispatcher = OrderingHarness.Create(dataSource, new FakeTransport(), OrderingHarness.Options(database, partitions: 4));
        Assert.Null(await dispatcher.CheckOrderingAsync(atStartup: true, ct));
        await dispatcher.RunOnceAsync(ct); // takes the partitions
        await DispatcherHarness.EnqueueAsync(database, 9, key: i => "ABC"[i % 3].ToString());
        await using var clock = await TestClock.InstallAsync(database);

        var claimed = await new OutboxStore(clock.DataSource).ClaimAsync(dispatcher.Owner, 100, TimeSpan.FromSeconds(30), ct, partitions: 4);

        Assert.Equal(3, claimed.Count);
        Assert.Equal(1, await clock.ReadsAsync());
    }

    // OrderingHarness copies the options so instances built from one object can differ; a property left out of the copy
    // would silently fall back to its default in every ordered test.
    [Fact]
    public void OrderingHarness_CopiaTodasAsOpcoes()
    {
        var options = new WaybillDispatcherOptions();
        foreach (var property in typeof(WaybillDispatcherOptions).GetProperties(BindingFlags.Public | BindingFlags.Instance).Where(p => p.CanWrite))
        {
            object value = property.PropertyType == typeof(int) ? 7
                : property.PropertyType == typeof(bool) ? true
                : property.PropertyType == typeof(TimeSpan) ? TimeSpan.FromSeconds(7)
                : property.PropertyType == typeof(string) ? "Host=copied"
                : throw new InvalidOperationException($"No sample value for {property.Name}.");
            property.SetValue(options, value);
        }
        options.MaxPerKey = 3;

        var copy = OrderingHarness.Copy(options);

        foreach (var property in typeof(WaybillDispatcherOptions).GetProperties(BindingFlags.Public | BindingFlags.Instance).Where(p => p.CanWrite))
            Assert.Equal(property.GetValue(options), property.GetValue(copy));
    }
}
