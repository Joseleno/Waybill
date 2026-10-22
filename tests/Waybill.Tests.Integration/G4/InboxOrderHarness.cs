using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;
using Waybill.EntityFrameworkCore;
using Waybill.Tests.Integration.G3;
using Waybill.Tests.Integration.Retencao;

namespace Waybill.Tests.Integration.G4;

/// <summary>Sequence verification in the inbox (stage 10): deliveries of ordered messages, as a consumer would make them.</summary>
public static class InboxOrderHarness
{
    public const string Handler = "receipts.invoice-paid";
    public const string Key = "order-42";

    /// <summary>The container of <see cref="TestDatabase.Services"/>, with debug logs captured.</summary>
    public static ServiceProvider Services(
        TestDatabase database, out LogSink logs, Action<Npgsql.EntityFrameworkCore.PostgreSQL.Infrastructure.NpgsqlDbContextOptionsBuilder>? npgsql = null,
        params Microsoft.EntityFrameworkCore.Diagnostics.IInterceptor[] interceptors)
    {
        logs = new LogSink { MinimumLevel = LogLevel.Debug };
        return database.Services(logs: logs, npgsql: npgsql, interceptors: interceptors);
    }

    /// <summary>
    /// Another session holding <c>waybill.inbox_keys</c> exclusively: anything that reads or writes it waits until the
    /// lock is dropped. Statistics would not do: other sessions report them late.
    /// </summary>
    public static async Task<NpgsqlConnection> LockInboxKeysAsync(TestDatabase database)
    {
        var locker = new NpgsqlConnection(database.ConnectionString);
        await locker.OpenAsync();
        await using var command = new NpgsqlCommand("BEGIN; LOCK TABLE waybill.inbox_keys IN ACCESS EXCLUSIVE MODE", locker);
        await command.ExecuteNonQueryAsync();
        return locker;
    }

    /// <summary>One delivery, in its own DI scope like a consumer would: the effect is a new invoice row.</summary>
    public static async Task<OrderedInboxResult> DeliverAsync(
        IServiceProvider services, Guid messageId, MessageOrder? order, HandlerCalls? calls = null, string handler = Handler)
    {
        await using var scope = services.CreateAsyncScope();
        var inbox = scope.ServiceProvider.GetRequiredService<IInbox<AppDbContext>>();
        return await inbox.ProcessInOrderAsync(handler, messageId, order, calls is null ? InboxHarness.ApplyEffect : calls.ApplyEffect);
    }

    /// <summary>A delivery of sequence <paramref name="sequence"/> of <see cref="Key"/>.</summary>
    public static Task<OrderedInboxResult> DeliverAsync(
        IServiceProvider services, Guid messageId, long sequence, HandlerCalls? calls = null, string handler = Handler) =>
        DeliverAsync(services, messageId, new MessageOrder(Key, sequence), calls, handler);

    /// <summary>The last message <paramref name="handler"/> applied for <paramref name="key"/>, or null.</summary>
    public static async Task<(long Sequence, Guid MessageId)?> LastAppliedAsync(TestDatabase database, string key = Key, string handler = Handler)
    {
        await using var connection = new NpgsqlConnection(database.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("SELECT last_seq, last_message_id FROM waybill.inbox_keys WHERE handler = $1 AND key = $2", connection)
        {
            Parameters = { new() { Value = handler }, new() { Value = key } },
        };
        await using var reader = await command.ExecuteReaderAsync();
        return await reader.ReadAsync() ? (reader.GetInt64(0), reader.GetGuid(1)) : null;
    }

    /// <summary>Inbox retention, run past every record: what lets a late copy through deduplication.</summary>
    public static async Task ForgetInboxRecordsAsync(TestDatabase database)
    {
        await database.ExecuteAsync("UPDATE waybill.inbox SET processed_at = processed_at - interval '31 days'");
        await using var dataSource = NpgsqlDataSource.Create(database.ConnectionString);
        var cleaner = RetentionHarness.Create(dataSource, RetentionHarness.Options(database, o => o.InboxRetention = TimeSpan.FromDays(30)));
        await cleaner.RunOnceAsync(TestContext.Current.CancellationToken);
        Assert.Equal(0, await database.InboxRowsAsync());
    }

    /// <summary>The regression warnings (EventId 50) and duplicates by sequence (EventId 51) the inbox logged.</summary>
    public static LogRecord[] InboxLogs(this LogSink logs, int eventId) =>
        [.. logs.Records.Where(r => r.EventId == eventId && r.Category.StartsWith("Waybill.", StringComparison.Ordinal))];
}

/// <summary>Counts the handler's runs: a rollback hides the effect of a handler that ran, not the run.</summary>
public sealed class HandlerCalls
{
    private int _count;

    public int Count => Volatile.Read(ref _count);

    public Task ApplyEffect(AppDbContext db, CancellationToken ct)
    {
        Interlocked.Increment(ref _count);
        return InboxHarness.ApplyEffect(db, ct);
    }
}
