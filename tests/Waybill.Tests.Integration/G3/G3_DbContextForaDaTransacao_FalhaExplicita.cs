using Microsoft.Extensions.DependencyInjection;
using Waybill.EntityFrameworkCore;

namespace Waybill.Tests.Integration.G3;

// The trap of DI: a handler that writes through another instance of the context (a new scope, a factory) commits on
// its own, outside the inbox transaction. It fails loudly instead, with what to do, and nothing is applied.
[Collection(PostgresCollection.Name)]
public sealed class G3_DbContextForaDaTransacao_FalhaExplicita(PostgresFixture postgres)
{
    [Fact]
    public async Task G3_DbContextForaDaTransacao_FalhaExplicita_ENadaAplica()
    {
        var database = await TestDatabase.CreateAsync(postgres);
        await using var services = database.Services();
        var messageId = Guid.CreateVersion7();

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => InboxHarness.DeliverAsync(services, "billing.mark-paid", messageId,
            async (_, ct) =>
            {
                await using var other = services.CreateAsyncScope();
                var detached = other.ServiceProvider.GetRequiredService<AppDbContext>();
                await InboxHarness.ApplyEffect(detached, ct);
                await detached.SaveChangesAsync(ct);
            }));

        Assert.Contains("outside the inbox transaction", error.Message);
        Assert.Contains("billing.mark-paid", error.Message);
        Assert.Equal(0, await database.EffectsAsync());
        Assert.Equal(0, await database.InboxRowsAsync());
    }

    [Fact]
    public async Task G3_ContextoComTransacaoAberta_Recusado()
    {
        var database = await TestDatabase.CreateAsync(postgres);
        await using var services = database.Services();
        await using var scope = services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await using var transaction = await context.Database.BeginTransactionAsync(TestContext.Current.CancellationToken);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            scope.ServiceProvider.GetRequiredService<IInbox<AppDbContext>>().ProcessAsync("billing.mark-paid", Guid.CreateVersion7(), InboxHarness.ApplyEffect, TestContext.Current.CancellationToken));
        Assert.Contains("owns the transaction", error.Message);
    }
}
