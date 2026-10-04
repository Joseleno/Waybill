using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Waybill.EntityFrameworkCore;

internal sealed partial class DbContextOutbox<TContext>(
    TContext context, IOptions<WaybillOptions> options, ILogger<DbContextOutbox<TContext>> logger)
    : IOutbox<TContext>, IDisposable
    where TContext : DbContext
{
    private bool _contextVerified;

    public Guid Enqueue<TMessage>(TMessage message, string? key = null, string? correlationId = null, string? tenantId = null)
        where TMessage : notnull
    {
        VerifyContext();
        var envelope = EnvelopeFactory.Create(options.Value, message, key, correlationId, tenantId);
        OutboxBuffer.For(context).Add(OutboxRecord.From(envelope));
        return envelope.Id;
    }

    // A missing mapping or interceptor would make enqueued messages silently never reach the table; fail at the
    // first Enqueue instead, with what to do.
    private void VerifyContext()
    {
        if (_contextVerified)
            return;

        if (context.Model.FindEntityType(typeof(OutboxRecord)) is null)
            throw new InvalidOperationException(
                $"{typeof(TContext).Name} does not map the Waybill outbox. Call modelBuilder.AddWaybillOutbox() in OnModelCreating.");

        var interceptors = context.GetService<IDbContextOptions>().FindExtension<CoreOptionsExtension>()?.Interceptors;
        if (interceptors?.Contains(WaybillSaveChangesInterceptor.Instance) != true)
            throw new InvalidOperationException(
                $"{typeof(TContext).Name} is not configured with Waybill. Call UseWaybill() on its DbContextOptionsBuilder.");

        _contextVerified = true;
    }

    public void Dispose()
    {
        if (OutboxBuffer.Find(context) is not { Pending.Count: > 0 } buffer)
            return;

        var count = buffer.Pending.Count;
        buffer.Clear();
        if (options.Value.ThrowOnPendingMessagesAtDispose)
            throw new InvalidOperationException(
                $"{count} message(s) were enqueued on {typeof(TContext).Name} but never saved: SaveChanges was not called, or it failed.");

        LogPendingMessages(logger, count, typeof(TContext).Name);
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Error,
        Message = "{Count} message(s) were enqueued on {Context} but never saved: SaveChanges was not called, or it failed. They were discarded.")]
    private static partial void LogPendingMessages(ILogger logger, int count, string context);
}
