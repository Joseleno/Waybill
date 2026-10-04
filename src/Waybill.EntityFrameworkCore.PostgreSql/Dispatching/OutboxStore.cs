using System.Text.Json;
using Npgsql;
using NpgsqlTypes;

namespace Waybill.EntityFrameworkCore.Dispatching;

internal sealed record ClaimedMessage(OutgoingMessage Message, long Fence);

/// <summary>The dispatcher's SQL against <c>waybill.outbox</c>, as proven in the stage 0 spike (ADR 0001).</summary>
internal sealed class OutboxStore(NpgsqlDataSource dataSource)
{
    // Row claim in one short statement (READ COMMITTED, autocommit). The "claimable" condition is repeated on the
    // locked level and on the outer UPDATE: when FOR UPDATE meets a row another transaction changed and committed,
    // PostgreSQL re-checks only the predicates on the locked table (EvalPlanQual).
    private const string ClaimSql = """
        UPDATE waybill.outbox o
        SET status = 'claimed', owner = $1, fence = o.fence + 1, lease_until = clock_timestamp() + $2
        WHERE o.id IN (
            SELECT id FROM waybill.outbox
            WHERE status IN ('pending', 'claimed')
              AND (status = 'pending' OR lease_until < clock_timestamp())
            ORDER BY id
            LIMIT $3
            FOR UPDATE SKIP LOCKED)
          AND (o.status = 'pending' OR (o.status = 'claimed' AND o.lease_until < clock_timestamp()))
        RETURNING o.id, o.type, o.key, o.payload, o.content_type, o.headers, o.created_at, o.fence
        """;

    // Every finishing statement is fenced: it only touches rows still claimed by this owner with this fence.
    private const string Fenced = "FROM unnest($2::uuid[], $3::bigint[]) AS m(id, fence) WHERE o.id = m.id AND o.fence = m.fence AND o.owner = $1 AND o.status = 'claimed'";

    private const string PublishedSql = $"UPDATE waybill.outbox o SET status = 'published', published_at = clock_timestamp(), lease_until = NULL {Fenced}";

    private const string RetrySql = $"UPDATE waybill.outbox o SET status = 'pending', lease_until = NULL {Fenced}";

    private const string ReturnedSql = """
        UPDATE waybill.outbox o
        SET attempts = o.attempts + 1,
            status = CASE WHEN o.attempts + 1 >= $5 THEN 'dlq' ELSE 'pending' END,
            dlq_reason = CASE WHEN o.attempts + 1 >= $5 THEN m.reason ELSE o.dlq_reason END,
            lease_until = NULL
        FROM unnest($2::uuid[], $3::bigint[], $4::text[]) AS m(id, fence, reason)
        WHERE o.id = m.id AND o.fence = m.fence AND o.owner = $1 AND o.status = 'claimed'
        """;

    private const string DlqSql = """
        UPDATE waybill.outbox o
        SET status = 'dlq', dlq_reason = m.reason, attempts = o.attempts + 1, lease_until = NULL
        FROM unnest($2::uuid[], $3::bigint[], $4::text[]) AS m(id, fence, reason)
        WHERE o.id = m.id AND o.fence = m.fence AND o.owner = $1 AND o.status = 'claimed'
        """;

    private const string ReleaseOwnedSql = "UPDATE waybill.outbox SET status = 'pending', lease_until = NULL WHERE owner = $1 AND status = 'claimed'";

    public async Task<List<ClaimedMessage>> ClaimAsync(string owner, int batchSize, TimeSpan lease, CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand(ClaimSql);
        command.Parameters.Add(new NpgsqlParameter { Value = owner });
        command.Parameters.Add(new NpgsqlParameter { Value = lease, NpgsqlDbType = NpgsqlDbType.Interval });
        command.Parameters.Add(new NpgsqlParameter { Value = batchSize });

        var claimed = new List<ClaimedMessage>(batchSize);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var message = new OutgoingMessage(
                MessageId: reader.GetGuid(0),
                Name: reader.GetString(1),
                Key: reader.IsDBNull(2) ? null : reader.GetString(2),
                Payload: reader.GetFieldValue<byte[]>(3),
                ContentType: reader.GetString(4),
                Headers: ParseHeaders(reader.IsDBNull(5) ? null : reader.GetString(5)),
                CreatedAt: reader.GetFieldValue<DateTimeOffset>(6));
            claimed.Add(new ClaimedMessage(message, reader.GetInt64(7)));
        }

        // RETURNING has no order guarantee; publish in id (UUIDv7, roughly enqueue) order.
        claimed.Sort((a, b) => a.Message.MessageId.CompareTo(b.Message.MessageId));
        return claimed;
    }

    /// <summary>Applies the outcome of a batch. Returns how many rows were fenced off (another owner or a newer claim).</summary>
    public async Task<int> FinishAsync(string owner, IReadOnlyList<(ClaimedMessage Claim, PublishResult Result)> outcomes, int maxReturns, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var batch = connection.CreateBatch();
        var expected = new List<int>();

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
                command.Parameters.Add(new NpgsqlParameter { Value = maxReturns });
            batch.BatchCommands.Add(command);
            expected.Add(rows.Count);
        }

        Add(PublishedSql, PublishStatus.Confirmed, withReason: false, withBudget: false);
        Add(RetrySql, PublishStatus.Retry, withReason: false, withBudget: false);
        Add(ReturnedSql, PublishStatus.Returned, withReason: true, withBudget: true);
        Add(DlqSql, PublishStatus.Defect, withReason: true, withBudget: false);

        await batch.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        var fenced = 0;
        for (var i = 0; i < batch.BatchCommands.Count; i++)
            fenced += expected[i] - batch.BatchCommands[i].RecordsAffected;
        return fenced;
    }

    /// <summary>Hands back every row still claimed by <paramref name="owner"/> (graceful shutdown).</summary>
    public async Task<int> ReleaseOwnedAsync(string owner, CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand(ReleaseOwnedSql);
        command.Parameters.Add(new NpgsqlParameter { Value = owner });
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
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
