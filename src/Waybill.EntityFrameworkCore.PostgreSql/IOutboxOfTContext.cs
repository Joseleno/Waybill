using Microsoft.EntityFrameworkCore;

namespace Waybill.EntityFrameworkCore;

/// <summary>
/// An <see cref="IOutbox"/> bound to the <typeparamref name="TContext"/> instance of the current scope: messages
/// enqueued here are inserted by that context's next successful <c>SaveChanges</c>, in its transaction.
/// </summary>
/// <typeparam name="TContext">The application's <see cref="DbContext"/>, whose model maps the outbox with <c>MapWaybillOutbox()</c>.</typeparam>
public interface IOutbox<TContext> : IOutbox
    where TContext : DbContext;
