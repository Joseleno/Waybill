using Microsoft.EntityFrameworkCore;
using Waybill.EntityFrameworkCore;

namespace Waybill.Tests.Integration.G3;

// G3 with an execution strategy that retries: the inbox commit succeeds on the server but the client sees a transient
// error, so the whole unit runs again. The retry finds its own inbox row and returns Duplicate; the effect is applied
// once. The caller acks either way.
[Collection(PostgresCollection.Name)]
public sealed class G3_RetryDuranteCommit_AplicaUmaVez(PostgresFixture postgres)
{
    [Fact]
    public async Task G3_RetryDuranteCommit_AplicaUmaVez_RetryVoltaComoDuplicata()
    {
        var database = await TestDatabase.CreateAsync(postgres);
        var lostCommitAck = new LoseFirstCommitAcknowledgement();
        await using var services = database.Services(
            npgsql: o => o.EnableRetryOnFailure(3, TimeSpan.FromMilliseconds(10), null),
            interceptors: lostCommitAck);
        var runs = 0;

        var result = await InboxHarness.DeliverAsync(services, "billing.mark-paid", Guid.CreateVersion7(), (db, ct) =>
        {
            runs++;
            return InboxHarness.ApplyEffect(db, ct);
        });

        Assert.True(lostCommitAck.Fired, "the simulated lost acknowledgement never happened");
        Assert.Equal(InboxResult.Duplicate, result);
        Assert.Equal(1, runs);
        Assert.Equal(1, await database.EffectsAsync());
        Assert.Equal(1, await database.InboxRowsAsync());
    }
}
