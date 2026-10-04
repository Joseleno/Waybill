using System.Text;

namespace Waybill.Tests.Unit;

public sealed class KeyHashTests
{
    // Reference vectors from Apache Kafka's UtilsTest.testMurmur2: the partition a key maps to must match Kafka's
    // default partitioner, so ordering by key_hash % P in v0.2 agrees with the Kafka transport.
    [Theory]
    [InlineData("21", -973932308)]
    [InlineData("foobar", -790332482)]
    [InlineData("a-little-bit-long-string", -985981536)]
    [InlineData("a-little-bit-longer-string", -1486304829)]
    [InlineData("lkjh234lh9fiuh90y23oiuhsafujhadof229phr9h19h89h8", -58897971)]
    [InlineData("abc", 479470107)]
    public void KeyHash_Murmur2_IgualAoKafka(string key, int expected)
    {
        Assert.Equal(expected, KeyHash.Murmur2(Encoding.UTF8.GetBytes(key)));
    }

    [Fact]
    public void KeyHash_NaoNegativoEEstavel()
    {
        var hash = KeyHash.Of("foobar");

        Assert.Equal(-790332482 & 0x7fffffff, hash);
        Assert.Equal(hash, KeyHash.Of("foobar"));
    }

    [Fact]
    public void KeyHash_SemChave_UsaOMessageIdDeFormaEstavel()
    {
        var id = Guid.Parse("01920000-0000-7000-8000-000000000001");

        Assert.Equal(KeyHash.Of(id), KeyHash.Of(id));
        Assert.True(KeyHash.Of(id) >= 0);
    }
}
