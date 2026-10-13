using Waybill.EntityFrameworkCore.Dispatching;

namespace Waybill.Tests.Unit;

// How a claimed batch becomes rounds (M ≥ 2, PLAN.md 8c-2): round k carries the k-th row of each key's series, so no
// call carries two ordered messages of one key, and a row that cannot be placed safely goes back unsent.
public sealed class RodadasTests
{
    private static int _ids;

    private static ClaimedMessage Row(string? key, long? sequence, int rank = 0, Guid? id = null) => new(
        new OutgoingMessage
        {
            MessageId = id ?? new Guid(Interlocked.Increment(ref _ids), 0, 0, new byte[8]),
            Name = "test.v1",
            Key = key,
            Sequence = sequence,
            Payload = new byte[] { 1 },
            ContentType = "application/json",
            Headers = new Dictionary<string, string>(),
            CreatedAt = DateTimeOffset.UnixEpoch,
        },
        Fence: 1, sequence, MoreOfKey: false, rank);

    [Fact]
    public void Rodadas_PorRank_UmaOrdenadaPorChaveEmCadaChamada()
    {
        var a1 = Row("A", 1); var a2 = Row("A", 2, 1); var a3 = Row("A", 3, 2);
        var b7 = Row("B", 7); var b8 = Row("B", 8, 1);

        var plan = Rounds.Build([a3, b8, a1, a2, b7]);

        Assert.Equal([[a1, b7], [a2, b8], [a3]], plan.Rounds.Select(r => r.ToList()));
        Assert.Empty(plan.Rejected);
    }

    // Rows without a sequence (no key, or written before ordering was on) are not ordered: all in the first round, also
    // next to a numbered head of the same key.
    [Fact]
    public void Rodadas_SemSequence_VaoTodasNaPrimeira()
    {
        var loose = Row(null, null); var old1 = Row("A", null); var old2 = Row("A", null);
        var a5 = Row("A", 5); var a6 = Row("A", 6, 1);

        var plan = Rounds.Build([a6, old2, loose, a5, old1]);

        Assert.Equal([[loose, old1, old2, a5], [a6]], plan.Rounds.Select(r => r.ToList()));
        Assert.All(plan.Rounds, round => Assert.True(round.Where(r => r.Sequence is not null).GroupBy(r => r.Message.Key).All(g => g.Count() == 1)));
    }

    // A guard against a state the claim does not produce: a key whose ranks have a hole. What comes after the hole goes
    // back unsent rather than out of its place.
    [Fact]
    public void Rodadas_BuracoDeRank_DevolveODepoisDoBuraco()
    {
        var a1 = Row("A", 1); var a3 = Row("A", 3, 2); var a4 = Row("A", 4, 3);

        var plan = Rounds.Build([a1, a3, a4]);

        Assert.Equal([[a1]], plan.Rounds.Select(r => r.ToList()));
        Assert.Equal([a3, a4], plan.Rejected.Select(r => r.Row));
    }

    // Another guard: the same row twice would be published twice in one batch and its marking counted wrong. The whole
    // key goes back unsent.
    [Fact]
    public void Rodadas_LinhaRepetida_DevolveAChaveInteira()
    {
        var id = Guid.NewGuid();
        var a1 = Row("A", 1); var a2 = Row("A", 2, 1, id); var again = Row("A", 2, 1, id); var b1 = Row("B", 1);

        var plan = Rounds.Build([a1, a2, again, b1]);

        Assert.Equal([[b1]], plan.Rounds.Select(r => r.ToList()));
        Assert.Equal([a1, a2, again], plan.Rejected.Select(r => r.Row).OrderBy(r => r.Sequence));
        Assert.All(plan.Rejected, r => Assert.Contains("twice", r.Reason));
    }

    // A round starts only with time left, and at least as much as the round before took: the next one, about as long,
    // would otherwise run past the batch's deadline and the lease. A round before which nothing ran (D = 0) still needs
    // time left: a call with no time at all could only time out, its outcome unknown.
    [Theory]
    [InlineData(5, 15, false)]
    [InlineData(15, 15, true)]
    [InlineData(16, 15, true)]
    [InlineData(0, 0, false)]
    [InlineData(1, 0, true)]
    [InlineData(-1, 0, false)]
    public void Rodadas_SoComecaComTempoParaOutraIgual(int remainingSeconds, int previousSeconds, bool starts) =>
        Assert.Equal(starts, Rounds.MayStart(TimeSpan.FromSeconds(remainingSeconds), TimeSpan.FromSeconds(previousSeconds)));
}
