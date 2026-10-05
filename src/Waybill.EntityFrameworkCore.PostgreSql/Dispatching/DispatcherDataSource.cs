using Npgsql;

namespace Waybill.EntityFrameworkCore.Dispatching;

/// <summary>The dispatcher's own data source, owned (and disposed) by the container.</summary>
internal sealed class DispatcherDataSource(NpgsqlDataSource value) : IAsyncDisposable, IDisposable
{
    public NpgsqlDataSource Value { get; } = value;

    public ValueTask DisposeAsync() => Value.DisposeAsync();

    public void Dispose() => Value.Dispose();
}
