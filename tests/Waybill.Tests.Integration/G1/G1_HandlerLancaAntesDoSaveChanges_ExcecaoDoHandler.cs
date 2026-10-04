using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Waybill.EntityFrameworkCore;

namespace Waybill.Tests.Integration.G1;

// The common case of a pending message is application code failing before SaveChanges. The caller must see that
// failure, not Waybill's: this is why reporting pending messages at dispose logs by default instead of throwing.
[Collection(PostgresCollection.Name)]
public sealed class G1_HandlerLancaAntesDoSaveChanges_ExcecaoDoHandler(PostgresFixture postgres)
{
    [Fact]
    public async Task G1_HandlerLancaAntesDoSaveChanges_ExcecaoDoHandler_ChegaAoChamador()
    {
        var database = await TestDatabase.CreateAsync(postgres);
        var logs = new LogSink();
        await using var services = database.Services(logs: logs);

        async Task Handler()
        {
            await using var scope = services.CreateAsyncScope();
            var outbox = scope.ServiceProvider.GetRequiredService<IOutbox<AppDbContext>>();
            outbox.Enqueue(new InvoicePaid(Guid.NewGuid(), 10m));
            throw new InvoiceAlreadyPaidException();
        }

        await Assert.ThrowsAsync<InvoiceAlreadyPaidException>(Handler);
        Assert.Single(logs.Entries, e => e.Level == LogLevel.Error);
        Assert.Equal(0, await database.OutboxCountAsync());
    }

    private sealed class InvoiceAlreadyPaidException() : Exception("invoice already paid");
}
