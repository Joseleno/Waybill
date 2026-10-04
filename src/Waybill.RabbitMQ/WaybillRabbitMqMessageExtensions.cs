using RabbitMQ.Client;

namespace Waybill.RabbitMQ;

/// <summary>Reads the Waybill envelope back from a delivered RabbitMQ message, for the consumer's inbox.</summary>
public static class WaybillRabbitMqMessageExtensions
{
    /// <summary>
    /// The Waybill message id carried in the AMQP <c>message_id</c> property. It repeats on every redelivery and is
    /// the key the inbox deduplicates on.
    /// </summary>
    /// <exception cref="InvalidOperationException">The message has no <c>message_id</c>, or it is not a Waybill id.</exception>
    public static Guid GetWaybillMessageId(this IReadOnlyBasicProperties properties)
    {
        ArgumentNullException.ThrowIfNull(properties);
        return Guid.TryParse(properties.MessageId, out var messageId)
            ? messageId
            : throw new InvalidOperationException(
                $"The message has no Waybill message id (message_id = '{properties.MessageId}'). Was it published through the Waybill outbox?");
    }
}
