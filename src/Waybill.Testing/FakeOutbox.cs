using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Waybill.EntityFrameworkCore;

namespace Waybill.Testing;

/// <summary>A message seen by <see cref="FakeOutbox{TContext}"/>.</summary>
/// <param name="Id">The message id fixed at enqueue time.</param>
/// <param name="Name">The registered message name.</param>
/// <param name="Key">The aggregate key, if any.</param>
/// <param name="Message">The enqueued message object.</param>
public sealed record FakeOutboxMessage(Guid Id, string Name, string? Key, object Message);

/// <summary>
/// In-memory <see cref="IOutbox{TContext}"/> for testing application code without PostgreSQL. It validates
/// messages exactly like the real outbox (registered type, size), and a message counts as saved after the next
/// successful <c>SaveChanges</c> of the same context. Disposing it with messages still pending fails the test.
/// </summary>
/// <remarks>
/// Limit: the fake treats a successful <c>SaveChanges</c> as the commit. It does not observe the commit or the
/// rollback of an explicit transaction; test those paths against the real outbox.
/// </remarks>
public sealed class FakeOutbox<TContext> : IOutbox<TContext>, IDisposable
    where TContext : DbContext
{
    private readonly TContext _context;
    private readonly WaybillOptions _options;
    private readonly List<FakeOutboxMessage> _pending = [];
    private readonly List<FakeOutboxMessage> _saved = [];
    private bool _mappingVerified;

    /// <summary>Creates a fake bound to <paramref name="context"/>, validating with the application's options.</summary>
    public FakeOutbox(TContext context, IOptions<WaybillOptions> options)
        : this(context, options?.Value!)
    {
    }

    /// <summary>Creates a fake bound to <paramref name="context"/>, validating with <paramref name="options"/>.</summary>
    public FakeOutbox(TContext context, WaybillOptions options)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(options);
        _context = context;
        _options = options;
        _context.SavedChanges += OnSavedChanges;
    }

    /// <summary>Messages enqueued and not yet saved.</summary>
    public IReadOnlyList<FakeOutboxMessage> Pending => _pending;

    /// <summary>Messages saved by a successful <c>SaveChanges</c>, in enqueue order.</summary>
    public IReadOnlyList<FakeOutboxMessage> Saved => _saved;

    /// <inheritdoc />
    public Guid Enqueue<TMessage>(TMessage message, string? key = null, string? correlationId = null, string? tenantId = null)
        where TMessage : notnull
    {
        // Same configuration checks as the real outbox (outbox mapped, no TransactionScope, no AutoTransactionBehavior.Never
        // without a transaction), so a suite that passes with the fake does not fail in production at the first Enqueue.
        DbContextOutbox<TContext>.Verify(_context, ref _mappingVerified);
        var envelope = EnvelopeFactory.Create(_options, message, key, correlationId, tenantId);
        _pending.Add(new FakeOutboxMessage(envelope.Id, envelope.Name, envelope.Key, message));
        return envelope.Id;
    }

    /// <summary>
    /// Asserts that a saved message of type <typeparamref name="TMessage"/> matching <paramref name="predicate"/>
    /// exists, and returns it.
    /// </summary>
    /// <exception cref="WaybillAssertionException">No saved message matches.</exception>
    public TMessage ShouldContain<TMessage>(Func<TMessage, bool>? predicate = null)
    {
        var match = _saved.Select(m => m.Message).OfType<TMessage>().FirstOrDefault(m => predicate?.Invoke(m) ?? true);
        return match ?? throw new WaybillAssertionException(
            $"Expected a saved {typeof(TMessage).Name}{(predicate is null ? "" : " matching the predicate")}, but found none. {Describe()}");
    }

    /// <summary>Asserts that no message was saved.</summary>
    /// <exception cref="WaybillAssertionException">Some message was saved.</exception>
    public void ShouldBeEmpty()
    {
        if (_saved.Count > 0)
            throw new WaybillAssertionException($"Expected no saved messages. {Describe()}");
    }

    /// <summary>Asserts that every enqueued message was saved.</summary>
    /// <exception cref="WaybillAssertionException">Some message is still pending.</exception>
    public void ShouldHaveNoPending()
    {
        if (_pending.Count > 0)
            throw new WaybillAssertionException(
                $"{_pending.Count} message(s) were enqueued on {typeof(TContext).Name} but never saved: SaveChanges was not called, or it failed. {Describe()}");
    }

    /// <summary>Stops observing the context and fails if any message is still pending.</summary>
    /// <exception cref="WaybillAssertionException">Some message is still pending.</exception>
    public void Dispose()
    {
        _context.SavedChanges -= OnSavedChanges;
        ShouldHaveNoPending();
    }

    private void OnSavedChanges(object? sender, SavedChangesEventArgs e)
    {
        _saved.AddRange(_pending);
        _pending.Clear();
    }

    private string Describe() =>
        $"Saved: [{string.Join(", ", _saved.Select(m => m.Name))}]. Pending: [{string.Join(", ", _pending.Select(m => m.Name))}].";
}

/// <summary>Thrown by <see cref="FakeOutbox{TContext}"/> assertions; independent of the test framework.</summary>
public sealed class WaybillAssertionException : Exception
{
    /// <summary>Creates the exception with the assertion's explanation.</summary>
    public WaybillAssertionException(string message)
        : base(message)
    {
    }
}
