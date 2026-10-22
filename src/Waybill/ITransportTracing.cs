namespace Waybill;

/// <summary>
/// What only the transport knows about a message's send span: the messaging system and where the message goes. Internal,
/// so <see cref="ITransport"/> does not change; the dispatcher names the span of a transport without it just <c>send</c>.
/// </summary>
internal interface ITransportTracing
{
    /// <summary>The destination as the semantic conventions name it for this system; null when there is none.</summary>
    string? Destination(OutgoingMessage message);

    /// <summary>Adds <c>messaging.system</c>, <c>messaging.destination.name</c> and the system's own attributes.</summary>
    void AddTags(OutgoingMessage message, ICollection<KeyValuePair<string, object?>> tags);
}
