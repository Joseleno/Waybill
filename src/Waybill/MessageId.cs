using System.Buffers.Binary;
using System.Security.Cryptography;

namespace Waybill;

/// <summary>
/// The <c>message_id</c>: a UUIDv7 (RFC 9562) made at Enqueue, that increases in the order ids are made within a process,
/// also inside one millisecond, where <see cref="Guid.CreateVersion7()"/> is random. A 12-bit counter in <c>rand_a</c>
/// orders the ids of one millisecond (RFC 9562, section 6.2, method 1); past 4096 of them, the next millisecond is
/// borrowed, and a clock that goes back never makes an id smaller. EF inserts a SaveChanges' rows in id order and the
/// ordering trigger numbers a key's rows in that order, so messages of one key enqueued in one transaction keep their
/// enqueue order (ADR 0008). The other 62 bits stay random.
/// </summary>
internal static class MessageId
{
    private const int MaxCounter = 0xFFF;
    private static readonly Lock Gate = new();
    private static long s_lastMilliseconds;
    private static int s_counter;

    public static Guid NewV7() => NewV7(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

    internal static Guid NewV7(long nowMilliseconds)
    {
        long milliseconds;
        int counter;
        lock (Gate)
        {
            if (nowMilliseconds > s_lastMilliseconds)
            {
                s_lastMilliseconds = nowMilliseconds;
                s_counter = 0;
            }
            else if (++s_counter > MaxCounter)
            {
                s_lastMilliseconds++;
                s_counter = 0;
            }
            milliseconds = s_lastMilliseconds;
            counter = s_counter;
        }

        Span<byte> bytes = stackalloc byte[16];
        BinaryPrimitives.WriteInt64BigEndian(bytes[..8], milliseconds << 16); // 48-bit timestamp in bytes 0..5
        bytes[6] = (byte)(0x70 | (counter >> 8));                           // version 7, counter high bits
        bytes[7] = (byte)counter;
        RandomNumberGenerator.Fill(bytes[8..]);
        bytes[8] = (byte)(0x80 | (bytes[8] & 0x3F));                        // RFC 9562 variant
        return new Guid(bytes, bigEndian: true);
    }
}
