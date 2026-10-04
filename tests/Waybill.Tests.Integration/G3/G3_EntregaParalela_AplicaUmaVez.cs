using Waybill.EntityFrameworkCore;

namespace Waybill.Tests.Integration.G3;

// G3 under concurrency: two deliveries of the same message at the same time (invoked directly; through the broker a
// queue does not hand one message to two consumers at once, but a redelivery can overlap a slow first attempt).
// The second INSERT waits for the first transaction, then sees the row: the effect applies once.
[Collection(PostgresCollection.Name)]
public sealed class G3_EntregaParalela_AplicaUmaVez(PostgresFixture postgres)
{
    [Fact]
    public async Task G3_EntregaParalela_AplicaUmaVez_SegundaEsperaASaiComoDuplicata()
    {
        var database = await TestDatabase.CreateAsync(postgres);
        await using var services = database.Services();
        var messageId = Guid.CreateVersion7();
        var firstInsideHandler = new TaskCompletionSource();
        var releaseFirst = new TaskCompletionSource();

        async Task SlowHandle(AppDbContext db, CancellationToken ct)
        {
            firstInsideHandler.TrySetResult();
            await releaseFirst.Task; // holds the first transaction open while the second delivery arrives
            await InboxHarness.ApplyEffect(db, ct);
        }

        var first = Task.Run(() => InboxHarness.DeliverAsync(services, "billing.mark-paid", messageId, SlowHandle));
        await firstInsideHandler.Task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        var second = Task.Run(() => InboxHarness.DeliverAsync(services, "billing.mark-paid", messageId));
        await Task.Delay(500, TestContext.Current.CancellationToken); // the second is now blocked on the first one's uncommitted inbox row
        Assert.False(second.IsCompleted, "the second delivery did not wait for the first transaction");

        releaseFirst.SetResult();
        Assert.Equal(InboxResult.Processed, await first);
        Assert.Equal(InboxResult.Duplicate, await second);

        Assert.Equal(1, await database.EffectsAsync());
        Assert.Equal(1, await database.InboxRowsAsync());
    }
}
