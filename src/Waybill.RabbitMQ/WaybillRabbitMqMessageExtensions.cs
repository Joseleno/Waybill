using System.Globalization;
using System.Text;
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

    /// <summary>
    /// The key and sequence of an ordered Waybill message, from its <c>waybill-key</c> and <c>waybill-sequence</c>
    /// headers, for the inbox's sequence verification. Null when the message is not ordered (no key, written or
    /// published with ordering off): it carries no <c>waybill-sequence</c>.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// The message has a <c>waybill-sequence</c> that is not a whole number from 1, or no <c>waybill-key</c> with it, or a
    /// key longer than 255 UTF-8 bytes.
    /// </exception>
    public static MessageOrder? GetWaybillOrder(this IReadOnlyBasicProperties properties)
    {
        ArgumentNullException.ThrowIfNull(properties);
        if (properties.Headers is not { } headers || !headers.TryGetValue(RabbitMqTransport.SequenceHeader, out var rawSequence) || rawSequence is null)
            return null;

        var text = HeaderText(rawSequence, RabbitMqTransport.SequenceHeader);
        if (!long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var sequence) || sequence < 1)
            throw new InvalidOperationException(
                $"The message's {RabbitMqTransport.SequenceHeader} header ('{text}') is not a sequence from 1. Was it published through the Waybill outbox?");
        if (!headers.TryGetValue(RabbitMqTransport.KeyHeader, out var rawKey) || rawKey is null)
            throw new InvalidOperationException(
                $"The message has a {RabbitMqTransport.SequenceHeader} header but no {RabbitMqTransport.KeyHeader}. Was it published through the Waybill outbox?");

        try
        {
            return new MessageOrder(HeaderText(rawKey, RabbitMqTransport.KeyHeader), sequence);
        }
        catch (ArgumentException exception)
        {
            throw new InvalidOperationException($"The message's {RabbitMqTransport.KeyHeader} header is not a Waybill key: {exception.Message}", exception);
        }
    }

    // Header strings arrive from the broker as byte[]; properties built in code may carry strings.
    private static string HeaderText(object value, string header) => value switch
    {
        byte[] bytes => Encoding.UTF8.GetString(bytes),
        string text => text,
        _ => throw new InvalidOperationException($"The message's {header} header is a {value.GetType().Name}, not a string."),
    };
}
