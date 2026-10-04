namespace Waybill;

/// <summary>
/// Enqueues messages to be written to the outbox in the same transaction as the application's data.
/// </summary>
/// <remarks>
/// <see cref="Enqueue{TMessage}"/> fixes the message id, checks that the message type is registered, serializes the
/// message and checks its size immediately, so any error surfaces to the caller before anything is written. The
/// message is persisted by the next successful <c>SaveChanges</c> of the bound unit of work, and only if its
/// transaction commits.
/// </remarks>
public interface IOutbox
{
    /// <summary>Enqueues <paramref name="message"/> and returns its message id (UUIDv7).</summary>
    /// <param name="message">The message. Its type must be registered with <see cref="WaybillOptions.AddMessage{TMessage}"/>.</param>
    /// <param name="key">Aggregate key. Messages of the same key will be ordered once ordering ships (v0.2).</param>
    /// <param name="correlationId">Optional correlation id carried in the envelope.</param>
    /// <param name="tenantId">Optional tenant id carried in the envelope.</param>
    /// <exception cref="InvalidOperationException">The type is not registered, or the payload exceeds <see cref="WaybillOptions.MaxPayloadBytes"/>.</exception>
    Guid Enqueue<TMessage>(TMessage message, string? key = null, string? correlationId = null, string? tenantId = null)
        where TMessage : notnull;
}
