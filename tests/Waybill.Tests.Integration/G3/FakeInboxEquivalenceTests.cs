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

    // A second delivery while the first is still in the handler: the real one waits on the inbox row; so must the fake.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FakeInbox_EntregaParalela_UmaProcessadaUmaDuplicata(bool fake)
    {
        var ct = TestContext.Current.CancellationToken;
        var database = await TestDatabase.CreateAsync(postgres);
        await using var services = fake ? FakeServices(database) : database.Services();
        var firstInsideHandler = new TaskCompletionSource();
        var releaseFirst = new TaskCompletionSource();
        try
        {
            var first = Task.Run(() => InboxHarness.DeliverAsync(services, "h1", MessageA, async (db, token) =>
            {
                firstInsideHandler.TrySetResult();
                await releaseFirst.Task;
                await InboxHarness.ApplyEffect(db, token);
            }), ct);
            await firstInsideHandler.Task.WaitAsync(TimeSpan.FromSeconds(30), ct);
            var second = Task.Run(() => InboxHarness.DeliverAsync(services, "h1", MessageA), ct);
            await Task.Delay(300, ct);

            releaseFirst.SetResult();
            Assert.Equal([InboxResult.Processed, InboxResult.Duplicate], [await first, await second]);
            Assert.Equal(1, await database.EffectsAsync());
        }
        finally
        {
            releaseFirst.TrySetResult();
        }
    }

    // Sequence verification (stage 10): (handler, message, sequence or none, handler fails?). The scripts that need
    // inbox retention to forget a record are left to the real inbox: the fake keeps every record.
    public static TheoryData<string, (string Handler, Guid Message, long? Sequence, bool Fails)[]> OrderedScripts => new()
    {
        { "em-ordem", [("h1", MessageA, 1, false), ("h1", MessageB, 2, false)] },
        { "gap", [("h1", MessageA, 2, false), ("h1", MessageB, 5, false)] },
        { "regressao", [("h1", MessageA, 2, false), ("h1", MessageB, 1, false), ("h1", MessageB, 1, false)] },
        { "reusada", [("h1", MessageA, 1, false), ("h1", MessageB, 1, false)] },
        { "reentrega-depois-da-seguinte", [("h1", MessageA, 1, false), ("h1", MessageB, 2, false), ("h1", MessageA, 1, false)] },
        { "falha-e-reentrega", [("h1", MessageA, 1, true), ("h1", MessageA, 1, false)] },
        { "dois-handlers", [("h1", MessageA, 5, false), ("h2", MessageB, 3, false)] },
        { "sem-ordem", [("h1", MessageA, null, false), ("h1", MessageA, null, false)] },
    };

    [Theory]
    [MemberData(nameof(OrderedScripts))]
    public async Task FakeInbox_EmOrdem_EquivalenteAoReal(string script, (string Handler, Guid Message, long? Sequence, bool Fails)[] deliveries)
    {
        var real = await RunOrdered(deliveries, fake: false);
        var fake = await RunOrdered(deliveries, fake: true);

        Assert.Equal(real.Results, fake.Results);
        Assert.Equal(real.Effects, fake.Effects);
        Assert.Equal(fake.Results.Count(r => r == nameof(OrderedInboxResult.Regressed)), fake.Regressions);
        Assert.NotEmpty(script);
    }

    // A delivery of a key while another of the same key is in the handler: the real one waits on the key's row, then
    // compares with what the first applied; so must the fake.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FakeInbox_EntregaParalelaDaMesmaChave_EsperaEComparaComAPrimeira(bool fake)
    {
        var ct = TestContext.Current.CancellationToken;
        var database = await TestDatabase.CreateAsync(postgres);
        await using var services = fake ? FakeServices(database) : database.Services();
        var firstInsideHandler = new TaskCompletionSource();
        var releaseFirst = new TaskCompletionSource();
        try
        {
            var first = Task.Run(() => DeliverOrderedAsync(services, "h1", MessageA, 5, async (db, token) =>
            {
                firstInsideHandler.TrySetResult();
                await releaseFirst.Task;
                await InboxHarness.ApplyEffect(db, token);
            }), ct);
            await firstInsideHandler.Task.WaitAsync(TimeSpan.FromSeconds(30), ct);
            var second = Task.Run(() => DeliverOrderedAsync(services, "h1", MessageB, 4, InboxHarness.ApplyEffect), ct);
            await Task.Delay(300, ct);
            Assert.False(second.IsCompleted);

            releaseFirst.SetResult();
            Assert.Equal([OrderedInboxResult.Processed, OrderedInboxResult.Regressed], [await first, await second]);
            Assert.Equal(1, await database.EffectsAsync());
        }
        finally
        {
            releaseFirst.TrySetResult();
        }
    }

    // A delivery cancelled while it waits for its key must not let the key go: it never held it. The real inbox only
    // rolls back; a fake that released the holder's lock would let a third delivery into the handler meanwhile.
    [Fact]
    public async Task FakeInbox_EsperaPelaChaveCancelada_NaoSoltaAChaveDeQuemEstaNoHandler()
    {
        var ct = TestContext.Current.CancellationToken;
        var database = await TestDatabase.CreateAsync(postgres);
        await using var services = FakeServices(database);
        var (holderInside, releaseHolder) = (new TaskCompletionSource(), new TaskCompletionSource());
        try
        {
            var holder = Task.Run(() => DeliverOrderedAsync(services, "h1", MessageA, 5, async (db, token) =>
            {
                holderInside.TrySetResult();
                await releaseHolder.Task;
                await InboxHarness.ApplyEffect(db, token);
            }), ct);
            await holderInside.Task.WaitAsync(TimeSpan.FromSeconds(30), ct);
            using var cancel = new CancellationTokenSource();
            var cancelled = Task.Run(() => DeliverOrderedAsync(services, "h1", MessageB, 6, InboxHarness.ApplyEffect, cancel.Token), ct);
            await Task.Delay(200, ct);
            await cancel.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);

            var third = Task.Run(() => DeliverOrderedAsync(services, "h1", Guid.CreateVersion7(), 4, InboxHarness.ApplyEffect), ct);
            await Task.Delay(300, ct);
            Assert.False(third.IsCompleted, "a third delivery got the key while the holder was still in its handler");

            releaseHolder.SetResult();
            Assert.Equal([OrderedInboxResult.Processed, OrderedInboxResult.Regressed], [await holder, await third]);
        }
        finally
        {
            releaseHolder.TrySetResult();
        }
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

    private async Task<(List<string> Results, long Effects, int Regressions)> RunOrdered(
        (string Handler, Guid Message, long? Sequence, bool Fails)[] deliveries, bool fake)
    {
        var database = await TestDatabase.CreateAsync(postgres);
        await using var services = fake ? FakeServices(database) : database.Services();
        var results = new List<string>();
        foreach (var (handler, message, sequence, fails) in deliveries)
        {
            try
            {
                var result = await DeliverOrderedAsync(services, handler, message, sequence, async (db, ct) =>
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
        return (results, await database.EffectsAsync(), fake ? services.GetRequiredService<FakeInboxMemory>().Regressions : 0);
    }

    private static async Task<OrderedInboxResult> DeliverOrderedAsync(
        IServiceProvider services, string handler, Guid messageId, long? sequence, Func<AppDbContext, CancellationToken, Task> handle, CancellationToken cancellationToken = default)
    {
        await using var scope = services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IInbox<AppDbContext>>().ProcessInOrderAsync(
            handler, messageId, sequence is { } s ? new MessageOrder("order-42", s) : null, handle, cancellationToken);
    }

    private static ServiceProvider FakeServices(TestDatabase database) =>
        new ServiceCollection()
            .AddDbContext<AppDbContext>(o => o.UseNpgsql(database.ConnectionString))
            .AddFakeWaybillInbox<AppDbContext>()
            .BuildServiceProvider();
}
