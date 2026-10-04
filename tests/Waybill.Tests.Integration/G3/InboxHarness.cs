using Microsoft.Extensions.DependencyInjection;
using Waybill.EntityFrameworkCore;

namespace Waybill.Tests.Integration.G3;

public static class InboxHarness
{
    /// <summary>One delivery, in its own DI scope like a consumer would: the effect is a new invoice row.</summary>
    public static async Task<InboxResult> DeliverAsync(
        IServiceProvider services, string handler, Guid messageId, Func<AppDbContext, CancellationToken, Task>? handle = null)
    {
        await using var scope = services.CreateAsyncScope();
        var inbox = scope.ServiceProvider.GetRequiredService<IInbox<AppDbContext>>();
        return await inbox.ProcessAsync(handler, messageId, handle ?? ApplyEffect);
    }

    public static Task ApplyEffect(AppDbContext db, CancellationToken ct)
    {
        db.Invoices.Add(new Invoice { Number = $"effect-{Guid.NewGuid():N}", Amount = 1m });
        return Task.CompletedTask;
    }

    public static Task<long> EffectsAsync(this TestDatabase database) =>
        database.ScalarAsync("SELECT count(*) FROM invoices WHERE \"Number\" LIKE 'effect-%'");

    public static Task<long> InboxRowsAsync(this TestDatabase database) => database.ScalarAsync("SELECT count(*) FROM waybill.inbox");
}
