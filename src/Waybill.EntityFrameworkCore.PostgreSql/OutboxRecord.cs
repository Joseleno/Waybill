namespace Waybill.EntityFrameworkCore;

/// <summary>
/// The columns the application's <c>DbContext</c> inserts into <c>waybill.outbox</c>. Everything else (status,
/// lease, fencing token, timestamps) takes the database defaults and belongs to the dispatcher.
/// </summary>
internal sealed class OutboxRecord
{
    public Guid Id { get; init; }
    public required string Type { get; init; }
    public string? Key { get; init; }
    public int KeyHash { get; init; }
    public required byte[] Payload { get; init; }
    public required string ContentType { get; init; }
    public string? Headers { get; init; }

    /// <summary>
    /// The distinct keys of the <c>SaveChanges</c> this row is saved by, when there are two or more; set by
    /// <see cref="OutboxKeyLockInterceptor"/> and cleared by the numbering trigger before the row is stored (ADR 0008).
    /// </summary>
    public string[]? LockKeys { get; set; }

    public static OutboxRecord From(MessageEnvelope envelope) => new()
    {
        Id = envelope.Id,
        Type = envelope.Name,
        Key = envelope.Key,
        KeyHash = envelope.KeyHash,
        Payload = envelope.Payload,
        ContentType = envelope.ContentType,
        Headers = envelope.Headers,
    };
}
