using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using Waybill.EntityFrameworkCore;
using Waybill.EntityFrameworkCore.Dispatching;

namespace Waybill.Tests.Integration.G2;

/// <summary>
/// Stands in for the broker. Behavior is a function of the call number; every message handed to it is recorded in
/// arrival order, whatever the outcome reported back.
/// </summary>
public sealed class FakeTransport(Func<int, IReadOnlyList<OutgoingMessage>, CancellationToken, Task<IReadOnlyList<PublishResult>>>? behavior = null)
    : ITransport
{
    private int _calls;

    public ConcurrentQueue<OutgoingMessage> Received { get; } = new();

    public int Calls => Volatile.Read(ref _calls);

    public static Task<IReadOnlyList<PublishResult>> ConfirmAll(IReadOnlyList<OutgoingMessage> batch) =>
        Task.FromResult<IReadOnlyList<PublishResult>>(batch.Select(_ => PublishResult.Confirmed).ToList());

    public Task<IReadOnlyList<PublishResult>> PublishAsync(IReadOnlyList<OutgoingMessage> batch, CancellationToken cancellationToken)
    {
        var call = Interlocked.Increment(ref _calls);
        foreach (var message in batch)
            Received.Enqueue(message);
        return behavior is null ? ConfirmAll(batch) : behavior(call, batch, cancellationToken);
    }
}

public static class DispatcherHarness
{
    public static WaybillDispatcherOptions Options(TestDatabase database, Action<WaybillDispatcherOptions>? configure = null)
    {
        var options = new WaybillDispatcherOptions { ConnectionString = database.ConnectionString };
        configure?.Invoke(options);
        return options;
    }

    /// <summary>A dispatcher instance over the test database, driven cycle by cycle by the test.</summary>
    internal static OutboxDispatcher Create(
        NpgsqlDataSource dataSource, ITransport transport, WaybillDispatcherOptions options, int maxPayloadBytes = 64 * 1024,
        TimeProvider? time = null) =>
        new(new OutboxStore(dataSource), transport,
            Microsoft.Extensions.Options.Options.Create(new WaybillOptions { MaxPayloadBytes = maxPayloadBytes }),
            Microsoft.Extensions.Options.Options.Create(options),
            NullLogger<OutboxDispatcher>.Instance, time);

    /// <summary>Writes <paramref name="count"/> messages through the real outbox, one transaction.</summary>
    public static async Task<List<Guid>> EnqueueAsync(TestDatabase database, int count, Func<int, InvoicePaid>? message = null, int maxPayloadBytes = 64 * 1024)
    {
        await using var services = database.Services(o => o.MaxPayloadBytes = maxPayloadBytes);
        await using var scope = services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var outbox = scope.ServiceProvider.GetRequiredService<IOutbox<AppDbContext>>();
        var ids = new List<Guid>(count);
        for (var i = 0; i < count; i++)
            ids.Add(outbox.Enqueue(message?.Invoke(i) ?? new InvoicePaid(Guid.NewGuid(), i), key: $"invoice-{i}", correlationId: $"corr-{i}"));
        await context.SaveChangesAsync();
        return ids;
    }

    public static Task<long> CountAsync(this TestDatabase database, string status) =>
        database.ScalarAsync($"SELECT count(*) FROM waybill.outbox WHERE status = '{status}'");
}
