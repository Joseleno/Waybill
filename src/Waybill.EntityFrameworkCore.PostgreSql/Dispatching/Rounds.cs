namespace Waybill.EntityFrameworkCore.Dispatching;

/// <summary>A row that goes back unsent, and why.</summary>
internal sealed record RejectedRow(ClaimedMessage Row, string Reason);

/// <summary>A claimed batch split into rounds, and the rows that cannot be placed safely in any.</summary>
internal sealed record RoundPlan(IReadOnlyList<IReadOnlyList<ClaimedMessage>> Rounds, IReadOnlyList<RejectedRow> Rejected);

/// <summary>
/// Splits a claimed batch into rounds (M ≥ 2, PLAN.md 8c-2): round k carries the rows of rank k, the k-th row after its
/// key's head, so no call carries two ordered messages of one key. Rows without a sequence are not ordered and all go in
/// the first round. Each round is in id order, as a batch of one round always was.
/// </summary>
internal static class Rounds
{
    /// <summary>
    /// Whether a round may start with <paramref name="remaining"/> of the batch's deadline left: only with time left, and at
    /// least as much as the previous round took (<paramref name="previous"/>), so the next one, about as long, ends before
    /// the deadline and the lease. With no time left a call could only time out, its outcome unknown.
    /// </summary>
    public static bool MayStart(TimeSpan remaining, TimeSpan previous) => remaining > TimeSpan.Zero && remaining >= previous;

    public static RoundPlan Build(IReadOnlyList<ClaimedMessage> claimed)
    {
        var rejected = new List<RejectedRow>();

        // The same row twice: published twice in one batch, its marking counted wrong. Never produced by the claim; the
        // whole key goes back, so nothing of it goes out of place.
        var twice = claimed.GroupBy(c => c.Message.MessageId).Where(g => g.Count() > 1).Select(g => g.First()).ToList();
        var keysTwice = twice.Where(c => c.Message.Key is not null).Select(c => c.Message.Key!).ToHashSet(StringComparer.Ordinal);
        var idsTwice = twice.Select(c => c.Message.MessageId).ToHashSet();
        bool Twice(ClaimedMessage c) => idsTwice.Contains(c.Message.MessageId) || (c.Message.Key is not null && keysTwice.Contains(c.Message.Key));
        foreach (var row in claimed.Where(Twice))
            rejected.Add(new RejectedRow(row, $"message {row.Message.MessageId} came twice in one claim; its key goes back unsent"));

        // A key's ordered rows must be ranked 0, 1, 2… without a hole; what comes after a hole goes back.
        var placed = new List<ClaimedMessage>();
        placed.AddRange(claimed.Where(c => !Twice(c) && c.Sequence is null));
        foreach (var key in claimed.Where(c => !Twice(c) && c.Sequence is not null).GroupBy(c => c.Message.Key))
        {
            var expected = 0;
            foreach (var row in key.OrderBy(c => c.Rank))
            {
                if (row.Rank == expected)
                {
                    placed.Add(row);
                    expected++;
                }
                else
                {
                    rejected.Add(new RejectedRow(row, $"rank {row.Rank} of key {row.Message.Key} came without rank {expected}; it goes back unsent"));
                }
            }
        }

        var rounds = placed
            .GroupBy(c => c.Sequence is null ? 0 : c.Rank)
            .OrderBy(g => g.Key)
            .Select(g => (IReadOnlyList<ClaimedMessage>)g.OrderBy(c => c.Message.MessageId).ToList())
            .ToList();
        return new RoundPlan(rounds, rejected);
    }
}
