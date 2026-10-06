using Npgsql;
using NpgsqlTypes;

namespace Waybill.EntityFrameworkCore.Retention;

/// <summary>Retention's SQL against <c>waybill.outbox</c> and <c>waybill.inbox</c> (ADR 0004).</summary>
internal sealed class RetentionStore(NpgsqlDataSource dataSource)
{
    // One batch in one short statement (autocommit). Only published rows past the retention, counted from published_at.
    // The id bound lets the search walk the primary key: a UUIDv7 carries its creation time in its first 48 bits, and
    // a row is created before it is published, so every row past the cutoff also has an id below the cutoff's.
    // A client clock running ahead only keeps a row longer; one running behind still meets the published_at test.
    // SKIP LOCKED: concurrent cleaners take different rows instead of waiting on each other. The status is repeated on
    // the DELETE, like the claim repeats its condition (ADR 0001).
    private const string OutboxSql = """
        WITH cutoff AS MATERIALIZED (SELECT clock_timestamp() - $1 AS at),
        expired AS MATERIALIZED (
            SELECT o.id FROM waybill.outbox o
            WHERE o.id < (SELECT (lpad(to_hex(floor(extract(epoch FROM at) * 1000)::bigint), 12, '0') || repeat('0', 20))::uuid FROM cutoff)
              AND o.status = 'published'
              AND o.published_at < (SELECT at FROM cutoff)
            ORDER BY o.id
            LIMIT $2
            FOR UPDATE OF o SKIP LOCKED)
        DELETE FROM waybill.outbox o
        USING expired e
        WHERE o.id = e.id AND o.status = 'published'
        """;

    // Inbox rows past the retention, counted from processed_at (ix_inbox_processed_at). A delivery racing the delete
    // waits on the row lock and, once it is gone, is processed again: the boundary of G3 that OPERATIONS.md documents.
    private const string InboxSql = """
        WITH expired AS MATERIALIZED (
            SELECT handler, message_id FROM waybill.inbox
            WHERE processed_at < clock_timestamp() - $1
            ORDER BY processed_at
            LIMIT $2
            FOR UPDATE SKIP LOCKED)
        DELETE FROM waybill.inbox i
        USING expired e
        WHERE i.handler = e.handler AND i.message_id = e.message_id
        """;

    public Task<int> DeleteOutboxBatchAsync(TimeSpan retention, int batchSize, CancellationToken cancellationToken) =>
        ExecuteAsync(OutboxSql, retention, batchSize, cancellationToken);

    public Task<int> DeleteInboxBatchAsync(TimeSpan retention, int batchSize, CancellationToken cancellationToken) =>
        ExecuteAsync(InboxSql, retention, batchSize, cancellationToken);

    /// <summary>The isolation level the deletes run under: the session default (<see cref="IsolationLevelCheck"/>).</summary>
    public Task<string> DefaultIsolationAsync(CancellationToken cancellationToken) =>
        IsolationLevelCheck.DefaultAsync(dataSource, cancellationToken);

    private async Task<int> ExecuteAsync(string sql, TimeSpan retention, int batchSize, CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand(sql);
        command.Parameters.Add(new NpgsqlParameter { Value = retention, NpgsqlDbType = NpgsqlDbType.Interval });
        command.Parameters.Add(new NpgsqlParameter { Value = batchSize });
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}
