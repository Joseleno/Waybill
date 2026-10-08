namespace Waybill.Tests.Unit;

// message_id is a UUIDv7 made at Enqueue. Ids made by one process increase in the order they are made, also within one
// millisecond: EF inserts a SaveChanges' rows in id order, and the trigger numbers a key's rows in that order (ADR 0008).
public sealed class MessageIdTests
{
    [Fact]
    public void MessageId_UUIDv7ComVarianteRfc()
    {
        var id = MessageId.NewV7();

        Assert.Equal(7, id.Version);
        Assert.Equal(0b10, id.ToByteArray(bigEndian: true)[8] >> 6);
        var milliseconds = Convert.ToInt64(Convert.ToHexString(id.ToByteArray(bigEndian: true), 0, 6), 16);
        Assert.InRange(milliseconds, DateTimeOffset.UtcNow.AddSeconds(-5).ToUnixTimeMilliseconds(), DateTimeOffset.UtcNow.AddSeconds(5).ToUnixTimeMilliseconds());
    }

    [Fact]
    public void MessageId_RajadaNoMesmoMilissegundo_CrescenteNaOrdemEmQueFoiFeito()
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var ids = Enumerable.Range(0, 10_000).Select(_ => MessageId.NewV7(now)).ToArray(); // past 4096: borrows the next milliseconds

        AssertStrictlyIncreasing(ids);
        Assert.Equal(10_000, ids.Distinct().Count());
    }

    [Fact]
    public void MessageId_RelogioQueVoltaAtras_ContinuaCrescente()
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var ids = new[] { MessageId.NewV7(now), MessageId.NewV7(now - 60_000), MessageId.NewV7(now - 1) };

        AssertStrictlyIncreasing(ids);
    }

    [Fact]
    public void MessageId_ConcorrenteEmVariasThreads_Unico()
    {
        var ids = new System.Collections.Concurrent.ConcurrentBag<Guid>();
        Parallel.For(0, 8, _ =>
        {
            for (var i = 0; i < 5_000; i++)
                ids.Add(MessageId.NewV7());
        });

        Assert.Equal(40_000, ids.Distinct().Count());
    }

    // By Guid.CompareTo (EF's key order, which sorts the INSERTs) and by bytes (PostgreSQL's uuid order).
    private static void AssertStrictlyIncreasing(Guid[] ids)
    {
        for (var i = 1; i < ids.Length; i++)
        {
            Assert.True(ids[i - 1].CompareTo(ids[i]) < 0, $"CompareTo: id {i} is not above id {i - 1}");
            Assert.True(ids[i - 1].ToByteArray(bigEndian: true).AsSpan().SequenceCompareTo(ids[i].ToByteArray(bigEndian: true)) < 0,
                $"bytes: id {i} is not above id {i - 1}");
        }
    }
}
