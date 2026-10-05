using Npgsql;

namespace Waybill.EntityFrameworkCore.Retention;

/// <summary>Retention's own data source, owned (and disposed) by the container.</summary>
internal sealed class RetentionDataSource(NpgsqlDataSource value) : IAsyncDisposable, IDisposable
{
    public NpgsqlDataSource Value { get; } = value;

    public ValueTask DisposeAsync() => Value.DisposeAsync();

    public void Dispose() => Value.Dispose();
}
