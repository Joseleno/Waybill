using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Waybill.EntityFrameworkCore;
using Waybill.Testing;

namespace Waybill.Tests.Integration.G3;

// The fake inbox is only useful if it agrees with the real one. The same deliveries run against both; the outcome is
// the sequence of results plus how many effects reached the database.
[Collection(PostgresCollection.Name)]
public sealed class FakeInboxEquivalenceTests(PostgresFixture postgres)
{
    private static readonly Guid MessageA = Guid.Parse("01920000-0000-7000-8000-00000000000a");
    private static readonly Guid MessageB = Guid.Parse("01920000-0000-7000-8000-00000000000b");

    // (handler, message, handler fails?)
    public static TheoryData<string, (string Handler, Guid Message, bool Fails)[]> Scripts => new()
    {
        { "duplicata", [("h1", MessageA, false), ("h1", MessageA, false), ("h1", MessageB, false)] },
        { "dois-handlers", [("h1", MessageA, false), ("h2", MessageA, false), ("h2", MessageA, false)] },
        { "falha-e-reentrega", [("h1", MessageA, true), ("h1", MessageA, false), ("h1", MessageA, false)] },
    };

    [Theory]
    [MemberData(nameof(Scripts))]
    public async Task FakeInbox_EquivalenteAoReal(string script, (string Handler, Guid Message, bool Fails)[] deliveries)
    {
        var real = await Run(deliveries, fake: false);
        var fake = await Run(deliveries, fake: true);

        Assert.Equal(real.Results, fake.Results);
        Assert.Equal(real.Effects, fake.Effects);
        Assert.NotEmpty(script);
    }

    [Fact]
    public async Task FakeInbox_ShouldHaveProcessed()
    {
        var database = await TestDatabase.CreateAsync(postgres);
        await using var services = FakeServices(database);
        await using (var scope = services.CreateAsyncScope())
            await scope.ServiceProvider.GetRequiredService<IInbox<AppDbContext>>().ProcessAsync("h1", MessageA, InboxHarness.ApplyEffect, TestContext.Current.CancellationToken);

        var memory = services.GetRequiredService<FakeInboxMemory>();
        memory.ShouldHaveProcessed("h1", MessageA);
        Assert.Throws<WaybillAssertionException>(() => memory.ShouldHaveProcessed("h2", MessageA));
    }

    private async Task<(List<string> Results, long Effects)> Run((string Handler, Guid Message, bool Fails)[] deliveries, bool fake)
    {
        var database = await TestDatabase.CreateAsync(postgres);
        await using var services = fake ? FakeServices(database) : database.Services();
        var results = new List<string>();
        foreach (var (handler, message, fails) in deliveries)
        {
            try
            {
                var result = await InboxHarness.DeliverAsync(services, handler, message, async (db, ct) =>
                {
                    await InboxHarness.ApplyEffect(db, ct);
                    if (fails)
                        throw new InvalidOperationException("handler failed");
                });
                results.Add(result.ToString());
            }
            catch (InvalidOperationException)
            {
                results.Add("Failed");
            }
        }
        return (results, await database.EffectsAsync());
    }

    private static ServiceProvider FakeServices(TestDatabase database) =>
        new ServiceCollection()
            .AddDbContext<AppDbContext>(o => o.UseNpgsql(database.ConnectionString))
            .AddFakeWaybillInbox<AppDbContext>()
            .BuildServiceProvider();
}
