using System.Runtime.CompilerServices;
using Microsoft.EntityFrameworkCore;

namespace Waybill.EntityFrameworkCore;

/// <summary>
/// Messages enqueued on a <see cref="DbContext"/> and not yet saved. Attached to the context instance itself, so the
/// stateless interceptor and the scoped outbox meet without sharing anything else.
/// </summary>
internal sealed class OutboxBuffer
{
    private static readonly ConditionalWeakTable<DbContext, OutboxBuffer> Buffers = new();

    private readonly List<OutboxRecord> _pending = [];

    public IReadOnlyList<OutboxRecord> Pending => _pending;

    public static OutboxBuffer For(DbContext context) => Buffers.GetOrCreateValue(context);

    public static OutboxBuffer? Find(DbContext context) => Buffers.TryGetValue(context, out var buffer) ? buffer : null;

    public void Add(OutboxRecord record) => _pending.Add(record);

    public void Clear() => _pending.Clear();
}
