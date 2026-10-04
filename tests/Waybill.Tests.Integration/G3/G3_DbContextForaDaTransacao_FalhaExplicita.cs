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

    // The guard lasts as long as the handler: work the handler forks and that saves after ProcessAsync returned is not
    // inside any inbox transaction and must not be refused.
    [Fact]
    public async Task G3_TarefaDerivadaDoHandler_GravaDepoisSemSerRecusada()
    {
        var ct = TestContext.Current.CancellationToken;
        var database = await TestDatabase.CreateAsync(postgres);
        await using var services = database.Services();
        var inboxDone = new TaskCompletionSource();
        Task? forked = null;

        await InboxHarness.DeliverAsync(services, "billing.mark-paid", Guid.CreateVersion7(), (_, _) =>
        {
            forked = Task.Run(async () =>
            {
                await inboxDone.Task;
                await using var scope = services.CreateAsyncScope();
                var later = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                await InboxHarness.ApplyEffect(later, ct);
                await later.SaveChangesAsync(ct);
            }, ct);
            return Task.CompletedTask;
        });
        inboxDone.SetResult();

        await forked!.WaitAsync(TimeSpan.FromSeconds(30), ct);
        Assert.Equal(1, await database.EffectsAsync());
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
