using System.Text.Json;
using Npgsql;
using NpgsqlTypes;

namespace Waybill.EntityFrameworkCore.Dispatching;

/// <summary>
/// A claimed row: its message, its fencing token and, when ordered by key, its sequence, whether more rows of its key wait
/// behind it, and its round: 0 for a head or an unordered row, k for the k-th row after its key's head in the batch (M ≥ 2).
/// </summary>
internal sealed record ClaimedMessage(OutgoingMessage Message, long Fence, long? Sequence = null, bool MoreOfKey = false, int Rank = 0);

/// <summary>The basic.return budget and the growing wait between returns (ADR 0006).</summary>
internal sealed record ReturnPolicy(int MaxReturns, TimeSpan Backoff, TimeSpan MaxBackoff)
{
    /// <summary>
    /// The smallest exponent at which the doubling wait reaches <see cref="MaxBackoff"/>. Bounding the exponent there keeps
    /// Backoff × 2^k below twice the ceiling, so it never overflows an interval, and never stops short of the ceiling.
    /// </summary>
    public int MaxExponent { get; } = Backoff <= TimeSpan.Zero || MaxBackoff <= Backoff
        ? 0
        : (int)Math.Ceiling(Math.Log2(MaxBackoff.Ticks / (double)Backoff.Ticks));

    public static ReturnPolicy From(WaybillDispatcherOptions options) =>
        new(options.MaxReturns, options.ReturnBackoff, options.MaxReturnBackoff);

    /// <summary>A returned message is claimable again at once, as in v0.1.</summary>
    public static ReturnPolicy Immediate(int maxReturns) => new(maxReturns, TimeSpan.Zero, TimeSpan.Zero);
}

/// <summary>What recording a batch's outcome did: rows fenced off, and rows that went to the DLQ with their reason.</summary>
internal sealed record FinishResult(int Fenced, IReadOnlyList<(Guid Id, string Reason)> DeadLettered);

/// <summary>The dispatcher's SQL against <c>waybill.outbox</c>, as proven in the stage 0 spike (ADR 0001).</summary>
internal sealed class OutboxStore(NpgsqlDataSource dataSource)
{
    // Row claim in one short statement (READ COMMITTED, autocommit). The candidates are locked in a MATERIALIZED CTE,
    // so the planner cannot re-run the LIMIT … SKIP LOCKED subquery per row. The "claimable" condition is repeated on
    // the UPDATE: when FOR UPDATE meets a row another transaction changed and committed, PostgreSQL re-checks only the
    // predicates on the locked table (EvalPlanQual). A row waiting after a basic.return is not claimable until its
    // next_attempt_at (ADR 0006); the index cannot filter on the clock, so waiting rows are read and passed over.
    // The candidates repeat ix_outbox_claimable's predicate word for word: a generic plan (auto-prepare) uses a partial
    // index only when the query's own predicate proves it applies.
    private static string Claim(string candidateFilter = "", string rowFilter = "") => $"""
        WITH candidates AS MATERIALIZED (
            SELECT id FROM waybill.outbox
            WHERE status IN ('pending', 'claimed') AND (sequence IS NULL OR is_head)
              AND (status = 'pending' OR lease_until < clock_timestamp())
              AND (next_attempt_at IS NULL OR next_attempt_at <= clock_timestamp()){candidateFilter}
            ORDER BY id
            LIMIT $3
            FOR UPDATE SKIP LOCKED)
        UPDATE waybill.outbox o
        SET status = 'claimed', owner = $1, fence = o.fence + 1, lease_until = clock_timestamp() + $2
        FROM candidates c
        WHERE o.id = c.id
          AND (o.status = 'pending' OR (o.status = 'claimed' AND o.lease_until < clock_timestamp()))
          AND (o.next_attempt_at IS NULL OR o.next_attempt_at <= clock_timestamp()){rowFilter}
        RETURNING o.id, o.type, o.key, o.payload, o.content_type, o.headers, o.created_at, o.fence
        """;

    // Without ordering: no keyed row while another dispatcher orders (waybill.settings exists). An instance that does not
    // order checks for that only at startup and every PartitionLease; until it stops, in a rolling deploy for one, it
    // must not claim keyed rows outside any partition. Uncorrelated, so evaluated once per statement.
    internal static readonly string ClaimSql = Claim(
        candidateFilter: """

              AND (key IS NULL OR NOT EXISTS (SELECT 1 FROM waybill.settings))
            """,
        rowFilter: """

          AND (o.key IS NULL OR NOT EXISTS (SELECT 1 FROM waybill.settings))
        """);

    // Ordering by key (ADRs 0007 and 0008).
    // - Partition: a keyed row is claimable only by the instance that holds its partition key_hash % $4, and only while
    //   that partition stays held for longer than the row lease it is about to take. The ownership is read in the
    //   statement's snapshot, not taken from the instance's memory, so an instance that paused after renewing and lost
    //   the partition meanwhile claims nothing of it. EvalPlanQual re-checks that subquery against the old snapshot,
    //   which is safe: nobody can take the partition before lease_until, and lease_until outlives the claim by a lease.
    // - Head: a row with a sequence is claimable only when no earlier row of its key is still pending, claimed or in the
    //   DLQ. Decided from the snapshot and not repeated on the UPDATE: an earlier row never leaves published or released
    //   (a trigger refuses it), and none appears after a later one is visible, since sequences follow commit order. With
    //   at most one row per key in the batch, a skipped or unclaimable head takes nothing behind it along.
    //   Asked as "the key's least unfinished sequence", which the planner answers by one descent of ix_outbox_key_sequence
    //   (min/max): as NOT EXISTS, a key dominating the statistics was probed by a sequential scan, row after row.
    // - Head pointer: only marked rows (is_head) are candidates, so a long queue behind a stopped or busy head is never
    //   read. The mark only narrows the candidates; the head is still decided here, from the snapshot.
    // - One instant per statement: the clock read once, for every row and for the lease it takes.
    // - The UPDATE finds its rows by primary key from an array: joined with the CTE, a generic plan hashed a sequential
    //   scan of the whole outbox (8c-2 review: 75 to 133 ms with 200 thousand rows waiting).
    // Both ordered claims are built from the fragments below, so a protection cannot be in one and missing in the other.
    private const string Now = "now AS MATERIALIZED (SELECT clock_timestamp() AS t)";

    private const string HeldPartition = """
        (o.key IS NULL OR EXISTS (
                SELECT 1 FROM waybill.outbox_partitions p
                WHERE p.partition = o.key_hash % $4 AND p.owner = $1 AND p.lease_until > now.t + $2))
        """;

    private const string HeadCandidates = $"""
        SELECT o.id, o.key, o.sequence FROM waybill.outbox o, now
            WHERE o.status IN ('pending', 'claimed') AND (o.sequence IS NULL OR o.is_head)
              AND (o.status = 'pending' OR o.lease_until < now.t)
              AND (o.next_attempt_at IS NULL OR o.next_attempt_at <= now.t)
              AND {HeldPartition}
              AND (o.sequence IS NULL OR o.sequence = (
                    SELECT min(h.sequence) FROM waybill.outbox h
                    WHERE h.key = o.key AND h.sequence IS NOT NULL AND h.status IN ('pending', 'claimed', 'dlq')))
            ORDER BY o.id
            LIMIT $3
            FOR UPDATE OF o SKIP LOCKED
        """;

    // Claimable at this instant: repeated on the locked row, where EvalPlanQual re-checks only the locked table's own
    // predicates, and on the UPDATE.
    private const string RowClaimable = """
        (o.status = 'pending' OR (o.status = 'claimed' AND o.lease_until < now.t))
              AND (o.next_attempt_at IS NULL OR o.next_attempt_at <= now.t)
        """;

    // The last column is each row's round as the claim decided it (0 for a head or an unordered row): the dispatcher builds
    // its rounds from it, so a successor whose head did not come back with it is refused, never sent first.
    private static string ClaimOrdered(string with, string claimed, bool ranked) => $"""
        WITH {Now},
        {with}
        UPDATE waybill.outbox o
        SET status = 'claimed', owner = $1, fence = o.fence + 1, lease_until = now.t + $2
        FROM now{(ranked ? ", ranks" : "")}
        WHERE o.id = ANY ((SELECT array_agg(id) FROM {claimed})::uuid[])
          AND {RowClaimable}
          AND {HeldPartition}
        RETURNING o.id, o.type, o.key, o.payload, o.content_type, o.headers, o.created_at, o.fence, o.sequence,
                  o.sequence IS NOT NULL AND o.sequence < (
                      SELECT max(s.sequence) FROM waybill.outbox s
                      WHERE s.key = o.key AND s.sequence IS NOT NULL AND s.status IN ('pending', 'claimed', 'dlq')),
                  {(ranked ? "(ranks.r ->> o.id::text)::int" : "0")} AS rank
        """;

    internal static readonly string ClaimOrderedSql = ClaimOrdered($"""
        heads AS MATERIALIZED (
            {HeadCandidates})
        """, "heads", ranked: false);

    // M ≥ 2 (PLAN.md 8c-2; $5 = M): the heads as above, then, of each head with a sequence, the next unfinished rows of its
    // key, read from ix_outbox_key_sequence, up to M − 1 and up to its share of what the heads left of the batch.
    // - series: claimable at this instant, judged in the statement's snapshot; a row that is not (another owner's live
    //   lease, a wait after basic.return, the DLQ) ends its key's series there.
    // - ranked: cut by round, every second row before any third, so a series is never cut in its middle.
    // - locked: locked one by one, by primary key, re-checked on the locked row; a row skipped or changed meanwhile
    //   (EvalPlanQual) ends its key's series there too.
    // The successors are never decided by the mark: is_head only narrows the heads.
    internal static readonly string ClaimSeriesSql = ClaimOrdered($"""
        heads AS MATERIALIZED (
            {HeadCandidates}),
        budget AS MATERIALIZED (
            SELECT $3 - count(*) AS n,
                   greatest(count(*) FILTER (WHERE h.sequence < (
                       SELECT max(m.sequence) FROM waybill.outbox m
                       WHERE m.key = h.key AND m.sequence IS NOT NULL AND m.status IN ('pending', 'claimed', 'dlq'))), 1) AS k
            FROM heads h),
        series AS (
            SELECT n.id, h.id AS head, n.rank, bool_and(n.claimable) OVER (PARTITION BY h.id ORDER BY n.rank) AS unbroken
            FROM heads h, budget b, now,
            LATERAL (
                SELECT s.id, row_number() OVER (ORDER BY s.sequence) AS rank,
                       (s.status = 'pending' OR (s.status = 'claimed' AND s.lease_until < now.t))
                         AND (s.next_attempt_at IS NULL OR s.next_attempt_at <= now.t) AS claimable
                FROM (SELECT x.id, x.sequence, x.status, x.lease_until, x.next_attempt_at FROM waybill.outbox x
                      WHERE x.key = h.key AND x.sequence > h.sequence AND x.sequence IS NOT NULL
                        AND x.status IN ('pending', 'claimed', 'dlq')
                      ORDER BY x.sequence
                      LIMIT least($5 - 1, (b.n + b.k - 1) / b.k)) s) n
            WHERE h.sequence IS NOT NULL AND b.n > 0),
        ranked AS MATERIALIZED (
            SELECT q.id, q.head, q.rank
            FROM (SELECT id, head, rank, row_number() OVER (ORDER BY rank, head) AS cut FROM series WHERE unbroken) q, budget b
            WHERE q.cut <= b.n),
        locked AS MATERIALIZED (
            SELECT l.id, r.head, r.rank
            FROM ranked r, now,
            LATERAL (SELECT o.id FROM waybill.outbox o
                     WHERE o.id = r.id
                       AND {RowClaimable}
                     FOR UPDATE SKIP LOCKED) l),
        claimed AS (
            SELECT id, 0::bigint AS rank FROM heads
            UNION ALL
            SELECT id, rank FROM (SELECT id, rank, row_number() OVER (PARTITION BY head ORDER BY rank) = rank AS unbroken FROM locked) p
            WHERE unbroken),
        ranks AS MATERIALIZED (SELECT jsonb_object_agg(id::text, rank) AS r FROM claimed)
        """, "claimed", ranked: true);

    // Every finishing statement is fenced (still claimed by this owner, with this fence) and returns what it changed.
    private const string Fenced = "o.id = m.id AND o.fence = m.fence AND o.owner = $1 AND o.status = 'claimed'";
    private const string Returning = "RETURNING o.id, o.status, o.dlq_reason";

    private const string PublishedSql =
        $"UPDATE waybill.outbox o SET status = 'published', published_at = clock_timestamp(), lease_until = NULL FROM unnest($2::uuid[], $3::bigint[]) AS m(id, fence) WHERE {Fenced} {Returning}";

    private const string RetrySql =
        $"UPDATE waybill.outbox o SET status = 'pending', lease_until = NULL FROM unnest($2::uuid[], $3::bigint[]) AS m(id, fence) WHERE {Fenced} {Returning}";

    // The k-th return waits min($6 × 2^(k−1), $7) by the database clock (ADR 0006); next_attempt_at is set only while a row
    // waits, never on a DLQ row, so requeueing from the DLQ needs nothing more. The exponent stops at $8
    // (ReturnPolicy.MaxExponent) before the multiplication: with MaxReturns in the hundreds, 2^k would overflow the
    // interval long before the ceiling applies.
    private const string ReturnedSql = $"""
        UPDATE waybill.outbox o
        SET attempts = o.attempts + 1,
            status = CASE WHEN o.attempts + 1 >= $5 THEN 'dlq' ELSE 'pending' END,
            dlq_reason = CASE WHEN o.attempts + 1 >= $5 THEN m.reason ELSE o.dlq_reason END,
            next_attempt_at = CASE WHEN o.attempts + 1 >= $5 THEN NULL
                                   ELSE clock_timestamp() + least($6 * power(2, least(o.attempts, $8)), $7) END,
            lease_until = NULL
        FROM unnest($2::uuid[], $3::bigint[], $4::text[]) AS m(id, fence, reason)
        WHERE {Fenced}
        {Returning}
        """;

    private const string DlqSql = $"""
        UPDATE waybill.outbox o
        SET status = 'dlq', dlq_reason = m.reason, attempts = o.attempts + 1, lease_until = NULL, next_attempt_at = NULL
        FROM unnest($2::uuid[], $3::bigint[], $4::text[]) AS m(id, fence, reason)
        WHERE {Fenced}
        {Returning}
        """;

    private const string ReleaseOwnedSql = "UPDATE waybill.outbox SET status = 'pending', lease_until = NULL WHERE owner = $1 AND status = 'claimed'";

    // The first row still in flight in claim order (ix_outbox_claimable), not min(created_at): with the broker down and a
    // large backlog that would read every pending row on every sample. The age comes from the database clock. With ordering
    // on, a row waiting behind its key's head is not in the index: the age is that of the oldest row a claim could take.
    internal const string OldestPendingAgeSql = """
        SELECT extract(epoch FROM clock_timestamp() - created_at)::float8
        FROM waybill.outbox
        WHERE status IN ('pending', 'claimed') AND (sequence IS NULL OR is_head)
        ORDER BY id
        LIMIT 1
        """;

    // The head pointer's safety net (ADR 0008). Walks, in key order from the first key or from after a cursor, the next
    // keys of a slice that have an unfinished numbered row, jumping from key to key in ix_outbox_key_sequence (a recursive
    // CTE, read only as far as the slice needs), and marks each key's first unfinished row left unmarked, in the partitions
    // of P this instance holds. Waybill itself never leaves one so: whatever this marks, something outside it changed the
    // outbox. Two statements, with and without the cursor: as one, with "cursor IS NULL OR key > cursor", a generic plan
    // (auto-prepare) could not seek to the cursor, and read the index from the first key on (issue #21).
    private static string RepairHeads(string cursor, int first) => $"""
        WITH RECURSIVE walk AS (
            (SELECT o.key FROM waybill.outbox o
             WHERE o.sequence IS NOT NULL AND o.status IN ('pending', 'claimed', 'dlq'){cursor}
             ORDER BY o.key LIMIT 1)
            UNION ALL
            SELECT (SELECT o.key FROM waybill.outbox o
                    WHERE o.sequence IS NOT NULL AND o.status IN ('pending', 'claimed', 'dlq') AND o.key > w.key
                    ORDER BY o.key LIMIT 1)
            FROM walk w
            WHERE w.key IS NOT NULL),
        slice AS MATERIALIZED (SELECT key FROM walk WHERE key IS NOT NULL LIMIT ${first}),
        repaired AS (
            UPDATE waybill.outbox o SET is_head = true
            FROM slice s
            WHERE o.id = (SELECT h.id FROM waybill.outbox h
                          WHERE h.key = s.key AND h.sequence IS NOT NULL AND h.status IN ('pending', 'claimed', 'dlq')
                          ORDER BY h.sequence LIMIT 1)
              AND o.is_head IS NOT TRUE AND o.key_hash % ${first + 1} = ANY (${first + 2})
            RETURNING o.key, o.sequence)
        SELECT (SELECT max(key) FROM slice), (SELECT count(*) FROM slice), r.key, r.sequence
        FROM (SELECT 1) AS one LEFT JOIN repaired r ON true
        """;

    /// <summary>The sweep from the first key: $1 slice, $2 P, $3 the partitions held.</summary>
    internal static readonly string RepairHeadsFirstSql = RepairHeads("", 1);

    /// <summary>The sweep from after a key: $1 that key, $2 slice, $3 P, $4 the partitions held.</summary>
    internal static readonly string RepairHeadsAfterSql = RepairHeads(" AND o.key > $1", 2);

    // With ordering off, the claim takes numbered rows only when marked, and nothing finishes the heads that would mark
    // the rest. The reset SQL marks them, but cannot see a row a producer wrote while settings existed and had not yet
    // committed; a dispatcher that does not order marks what is left. Empty once the numbered backlog drained.
    private const string MarkNumberedSql = """
        UPDATE waybill.outbox SET is_head = true
        WHERE sequence IS NOT NULL AND status IN ('pending', 'claimed', 'dlq') AND is_head IS NOT TRUE
        """;

    /// <summary>Marks every unfinished numbered row claimable (ordering off); returns how many it marked.</summary>
    public async Task<int> MarkNumberedClaimableAsync(CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand(MarkNumberedSql);
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Repairs one slice of keys after <paramref name="after"/> (null: from the first key); see <see cref="RepairHeadsAfterSql"/>.</summary>
    /// <returns>Where the next slice starts (null once the walk reached the last key), and the rows marked.</returns>
    public async Task<(string? Next, List<(string Key, long Sequence)> Repaired)> RepairHeadsAsync(
        string? after, int slice, int partitions, int[] held, CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand(after is null ? RepairHeadsFirstSql : RepairHeadsAfterSql);
        if (after is not null)
            command.Parameters.Add(new NpgsqlParameter { Value = after, NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Text });
        command.Parameters.Add(new NpgsqlParameter { Value = slice });
        command.Parameters.Add(new NpgsqlParameter { Value = partitions });
        command.Parameters.Add(new NpgsqlParameter { Value = held });
        string? last = null;
        long walked = 0;
        var repaired = new List<(string, long)>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            last = reader.IsDBNull(0) ? null : reader.GetString(0);
            walked = reader.GetInt64(1);
            if (!reader.IsDBNull(2))
                repaired.Add((reader.GetString(2), reader.GetInt64(3)));
        }
        return (walked < slice ? null : last, repaired);
    }

    /// <summary>
    /// Claims up to a batch. With <c>partitions</c> (P, ordering by key), only keyed rows of partitions this owner holds
    /// (ADR 0007), and of each key its head (ADR 0008) and, with <paramref name="maxPerKey"/> (M) above 1, up to M − 1
    /// rows after it (PLAN.md 8c-2), ranked by sequence.
    /// </summary>
    public async Task<List<ClaimedMessage>> ClaimAsync(
        string owner, int batchSize, TimeSpan lease, CancellationToken cancellationToken, int? partitions = null, int maxPerKey = 1)
    {
        var series = partitions is not null && maxPerKey > 1 && batchSize > 1;
        await using var command = dataSource.CreateCommand(partitions is null ? ClaimSql : series ? ClaimSeriesSql : ClaimOrderedSql);
        command.Parameters.Add(new NpgsqlParameter { Value = owner });
        command.Parameters.Add(new NpgsqlParameter { Value = lease, NpgsqlDbType = NpgsqlDbType.Interval });
        command.Parameters.Add(new NpgsqlParameter { Value = batchSize });
        if (partitions is not null)
            command.Parameters.Add(new NpgsqlParameter { Value = partitions.Value });
        if (series)
            command.Parameters.Add(new NpgsqlParameter { Value = maxPerKey });

        var claimed = new List<ClaimedMessage>(batchSize);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            long? sequence = reader.FieldCount > 8 && !reader.IsDBNull(8) ? reader.GetInt64(8) : null; // ordered claim only
            var message = new OutgoingMessage
            {
                MessageId = reader.GetGuid(0),
                Name = reader.GetString(1),
                Key = reader.IsDBNull(2) ? null : reader.GetString(2),
                Payload = reader.GetFieldValue<byte[]>(3),
                ContentType = reader.GetString(4),
                Headers = ParseHeaders(reader.IsDBNull(5) ? null : reader.GetString(5)),
                CreatedAt = reader.GetFieldValue<DateTimeOffset>(6),
                Sequence = sequence,
            };
            claimed.Add(reader.FieldCount > 8
                ? new ClaimedMessage(message, reader.GetInt64(7), sequence, reader.GetBoolean(9), reader.GetInt32(10))
                : new ClaimedMessage(message, reader.GetInt64(7)));
        }

        // RETURNING has no order guarantee; publish round by round, each in id (UUIDv7, roughly enqueue) order.
        // Guid.CompareTo matches PostgreSQL's uuid order.
        claimed.Sort((a, b) => a.Rank != b.Rank ? a.Rank.CompareTo(b.Rank) : a.Message.MessageId.CompareTo(b.Message.MessageId));
        return claimed;
    }

    /// <summary>Applies the outcome of a batch in one transaction.</summary>
    public async Task<FinishResult> FinishAsync(
        string owner, IReadOnlyList<(ClaimedMessage Claim, PublishResult Result)> outcomes, ReturnPolicy returns, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var batch = connection.CreateBatch();

        void Add(string sql, PublishStatus status, bool withReason, bool withBudget)
        {
            var rows = outcomes.Where(o => o.Result.Status == status).ToList();
            if (rows.Count == 0)
                return;
            var command = new NpgsqlBatchCommand(sql);
            command.Parameters.Add(new NpgsqlParameter { Value = owner });
            command.Parameters.Add(new NpgsqlParameter { Value = rows.Select(r => r.Claim.Message.MessageId).ToArray() });
            command.Parameters.Add(new NpgsqlParameter { Value = rows.Select(r => r.Claim.Fence).ToArray() });
            if (withReason)
                command.Parameters.Add(new NpgsqlParameter { Value = rows.Select(r => r.Result.Reason ?? status.ToString()).ToArray() });
            if (withBudget)
            {
                command.Parameters.Add(new NpgsqlParameter { Value = returns.MaxReturns });
                command.Parameters.Add(new NpgsqlParameter { Value = returns.Backoff, NpgsqlDbType = NpgsqlDbType.Interval });
                command.Parameters.Add(new NpgsqlParameter { Value = returns.MaxBackoff, NpgsqlDbType = NpgsqlDbType.Interval });
                command.Parameters.Add(new NpgsqlParameter { Value = returns.MaxExponent });
            }
            batch.BatchCommands.Add(command);
        }

        Add(PublishedSql, PublishStatus.Confirmed, withReason: false, withBudget: false);
        Add(RetrySql, PublishStatus.Retry, withReason: false, withBudget: false);
        Add(ReturnedSql, PublishStatus.Returned, withReason: true, withBudget: true);
        Add(DlqSql, PublishStatus.Defect, withReason: true, withBudget: false);
        if (batch.BatchCommands.Count == 0)
            return new FinishResult(0, []);

        var applied = 0;
        var deadLettered = new List<(Guid, string)>();
        await using (var reader = await batch.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            do
            {
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    applied++;
                    if (reader.GetString(1) == "dlq")
                        deadLettered.Add((reader.GetGuid(0), reader.GetString(2)));
                }
            } while (await reader.NextResultAsync(cancellationToken).ConfigureAwait(false));
        }

        return new FinishResult(outcomes.Count - applied, deadLettered);
    }

    /// <summary>Hands back every row still claimed by <paramref name="owner"/> (graceful shutdown).</summary>
    public async Task<int> ReleaseOwnedAsync(string owner, CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand(ReleaseOwnedSql);
        command.Parameters.Add(new NpgsqlParameter { Value = owner });
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The isolation level the claim runs under: the session default (<see cref="IsolationLevelCheck"/>).</summary>
    public Task<string> DefaultIsolationAsync(CancellationToken cancellationToken) =>
        IsolationLevelCheck.DefaultAsync(dataSource, cancellationToken);

    // Ordering by key: a DLQ row with a sequence is its key's head until it is requeued or released, so the key is
    // stopped (ADR 0008). Null while ordering is off: old DLQ rows then stop nothing. Read through ix_outbox_blocked_keys.
    private const string BlockedKeysSql = """
        SELECT CASE WHEN EXISTS (SELECT 1 FROM waybill.settings)
                    THEN (SELECT count(DISTINCT key) FROM waybill.outbox WHERE status = 'dlq' AND sequence IS NOT NULL) END
        """;

    /// <summary>How many keys a DLQ row stops; null while ordering by key is off.</summary>
    public async Task<long?> BlockedKeysAsync(CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand(BlockedKeysSql);
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is long blocked ? blocked : null;
    }

    /// <summary>Seconds since the oldest message not yet published was written; 0 when there is none.</summary>
    public async Task<double> OldestPendingAgeAsync(CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand(OldestPendingAgeSql);
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is double age ? Math.Max(age, 0) : 0;
    }

    private static Dictionary<string, string> ParseHeaders(string? json)
    {
        var headers = new Dictionary<string, string>(StringComparer.Ordinal);
        if (json is null)
            return headers;

        using var document = JsonDocument.Parse(json);
        foreach (var property in document.RootElement.EnumerateObject())
        {
            if (property.Value.ValueKind == JsonValueKind.String)
                headers[property.Name] = property.Value.GetString()!;
        }
        return headers;
    }
}
