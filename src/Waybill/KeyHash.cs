using System.Buffers.Binary;
using System.Text;

namespace Waybill;

/// <summary>
/// Stable, non-negative hash of the aggregate key: Kafka's murmur2 masked to 31 bits, so that a future partition
/// is <c>key_hash % P</c> and matches Kafka's default partitioner.
/// </summary>
internal static class KeyHash
{
    public static int Of(string key) => Positive(Murmur2(Encoding.UTF8.GetBytes(key)));

    public static int Of(Guid messageId)
    {
        Span<byte> bytes = stackalloc byte[16];
        messageId.TryWriteBytes(bytes, bigEndian: true, out _);
        return Positive(Murmur2(bytes));
    }

    private static int Positive(int hash) => hash & 0x7fffffff;

    // Port of org.apache.kafka.common.utils.Utils.murmur2.
    internal static int Murmur2(ReadOnlySpan<byte> data)
    {
        const uint seed = 0x9747b28c;
        const uint m = 0x5bd1e995;
        const int r = 24;

        var length = data.Length;
        var h = seed ^ (uint)length;
        var whole = length & ~3;

        for (var i = 0; i < whole; i += 4)
        {
            var k = BinaryPrimitives.ReadUInt32LittleEndian(data[i..]);
            k *= m;
            k ^= k >> r;
            k *= m;
            h *= m;
            h ^= k;
        }

        switch (length & 3)
        {
            case 3:
                h ^= (uint)data[whole + 2] << 16;
                goto case 2;
            case 2:
                h ^= (uint)data[whole + 1] << 8;
                goto case 1;
            case 1:
                h ^= data[whole];
                h *= m;
                break;
        }

        h ^= h >> 13;
        h *= m;
        h ^= h >> 15;
        return (int)h;
    }
}
