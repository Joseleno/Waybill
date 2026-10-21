using System.Text;

namespace Waybill;

/// <summary>
/// Where an ordered message stands among its key's messages: the aggregate key and its <c>sequence</c> (ADR 0008), as the
/// message carries them. The consumer's inbox uses it to detect a message older than one it already applied for the key.
/// </summary>
/// <remarks><c>default</c> is not a valid order: its <see cref="Key"/> is null, and the inbox refuses it.</remarks>
public readonly record struct MessageOrder
{
    /// <summary>The order of a received message.</summary>
    /// <param name="key">The aggregate key: not null, at most 255 UTF-8 bytes. An empty key is a key like any other, as at enqueue time.</param>
    /// <param name="sequence">The message's position among its key's messages, from 1.</param>
    /// <exception cref="ArgumentNullException"><paramref name="key"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="key"/> is longer than 255 UTF-8 bytes.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="sequence"/> is less than 1.</exception>
    public MessageOrder(string key, long sequence)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (Encoding.UTF8.GetByteCount(key) > EnvelopeFactory.MaxShortStringBytes)
            throw new ArgumentException($"The key '{key[..Math.Min(key.Length, 40)]}…' is longer than {EnvelopeFactory.MaxShortStringBytes} UTF-8 bytes.", nameof(key));
        ArgumentOutOfRangeException.ThrowIfLessThan(sequence, 1);
        Key = key;
        Sequence = sequence;
    }

    /// <summary>The aggregate key.</summary>
    public string Key { get; }

    /// <summary>The message's position among its key's messages, from 1, in commit order.</summary>
    public long Sequence { get; }
}
