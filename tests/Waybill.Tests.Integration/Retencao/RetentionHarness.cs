using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Waybill.EntityFrameworkCore.Retention;

namespace Waybill.Tests.Integration.Retencao;

public static class RetentionHarness
{
    public static WaybillRetentionOptions Options(TestDatabase database, Action<WaybillRetentionOptions>? configure = null)
    {
        var options = new WaybillRetentionOptions { ConnectionString = database.ConnectionString };
        configure?.Invoke(options);
        return options;
    }

    /// <summary>A cleaner over the test database, driven pass by pass by the test.</summary>
    internal static RetentionCleaner Create(NpgsqlDataSource dataSource, WaybillRetentionOptions options, LogSink? logs = null) =>
        new(new RetentionStore(dataSource), Microsoft.Extensions.Options.Options.Create(options),
            logs is null ? NullLogger<RetentionCleaner>.Instance : new LoggerFactory([logs]).CreateLogger<RetentionCleaner>());

    /// <summary>
    /// Writes one outbox row as it would be after its life so far: the id is a UUIDv7 from <paramref name="createdAgo"/>,
    /// like the client would have generated then, and <paramref name="publishedAgo"/> sets published_at.
    /// </summary>
    public static async Task<Guid> InsertOutboxAsync(
        this TestDatabase database, string status, TimeSpan createdAgo, TimeSpan? publishedAgo = null)
    {
        var id = Guid.CreateVersion7(DateTimeOffset.UtcNow - createdAgo);
        await using var connection = new NpgsqlConnection(database.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("""
            INSERT INTO waybill.outbox (id, type, key_hash, payload, content_type, status, owner, fence, lease_until, created_at, published_at, dlq_reason)
            VALUES ($1, 'billing.invoice-paid.v1', 0, '\x7b7d', 'application/json', $2,
                    CASE WHEN $2 = 'claimed' THEN 'gone-instance' END,
                    CASE WHEN $2 = 'pending' THEN 0 ELSE 1 END,
                    CASE WHEN $2 = 'claimed' THEN clock_timestamp() - interval '1 minute' END,
                    clock_timestamp() - $3,
                    clock_timestamp() - $4,
                    CASE WHEN $2 = 'dlq' THEN 'test defect' END)
            """, connection);
        command.Parameters.Add(new NpgsqlParameter { Value = id });
        command.Parameters.Add(new NpgsqlParameter { Value = status });
        command.Parameters.Add(new NpgsqlParameter { Value = createdAgo });
        command.Parameters.Add(new NpgsqlParameter { Value = (object?)publishedAgo ?? DBNull.Value, NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Interval });
        await command.ExecuteNonQueryAsync();
        return id;
    }

    /// <summary><paramref name="count"/> published outbox rows, created and published <paramref name="ago"/>, in one statement.</summary>
    public static async Task InsertPublishedOutboxBulkAsync(this TestDatabase database, int count, TimeSpan ago)
    {
        var ids = Enumerable.Range(0, count).Select(i => Guid.CreateVersion7(DateTimeOffset.UtcNow - ago - TimeSpan.FromMilliseconds(i))).ToArray();
        await using var connection = new NpgsqlConnection(database.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("""
            INSERT INTO waybill.outbox (id, type, key_hash, payload, content_type, status, fence, created_at, published_at)
            SELECT id, 'billing.invoice-paid.v1', 0, '\x7b7d', 'application/json', 'published', 1, clock_timestamp() - $2, clock_timestamp() - $2
            FROM unnest($1::uuid[]) AS id
            """, connection);
        command.Parameters.Add(new NpgsqlParameter { Value = ids });
        command.Parameters.Add(new NpgsqlParameter { Value = ago });
        await command.ExecuteNonQueryAsync();
    }

    /// <summary><paramref name="count"/> inbox rows processed <paramref name="ago"/>, in one statement.</summary>
    public static async Task InsertInboxBulkAsync(this TestDatabase database, int count, TimeSpan ago)
    {
        await using var connection = new NpgsqlConnection(database.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "INSERT INTO waybill.inbox (handler, message_id, processed_at) SELECT 'bulk', gen_random_uuid(), clock_timestamp() - $2 FROM generate_series(1, $1)",
            connection);
        command.Parameters.Add(new NpgsqlParameter { Value = count });
        command.Parameters.Add(new NpgsqlParameter { Value = ago });
        await command.ExecuteNonQueryAsync();
    }

    /// <summary>One inbox row processed <paramref name="processedAgo"/> ago.</summary>
    public static async Task InsertInboxAsync(this TestDatabase database, string handler, Guid messageId, TimeSpan processedAgo)
    {
        await using var connection = new NpgsqlConnection(database.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "INSERT INTO waybill.inbox (handler, message_id, processed_at) VALUES ($1, $2, clock_timestamp() - $3)", connection);
        command.Parameters.Add(new NpgsqlParameter { Value = handler });
        command.Parameters.Add(new NpgsqlParameter { Value = messageId });
        command.Parameters.Add(new NpgsqlParameter { Value = processedAgo });
        await command.ExecuteNonQueryAsync();
    }

    public static async Task<HashSet<Guid>> OutboxIdsAsync(this TestDatabase database)
    {
        await using var connection = new NpgsqlConnection(database.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("SELECT id FROM waybill.outbox", connection);
        await using var reader = await command.ExecuteReaderAsync();
        var ids = new HashSet<Guid>();
        while (await reader.ReadAsync())
            ids.Add(reader.GetGuid(0));
        return ids;
    }
}
