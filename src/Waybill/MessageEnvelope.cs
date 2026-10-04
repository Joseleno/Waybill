namespace Waybill;

/// <summary>A message as it is written to the outbox: serialized, validated and with its id fixed.</summary>
internal sealed record MessageEnvelope(
    Guid Id,
    string Name,
    string? Key,
    int KeyHash,
    byte[] Payload,
    string ContentType,
    string? Headers);
