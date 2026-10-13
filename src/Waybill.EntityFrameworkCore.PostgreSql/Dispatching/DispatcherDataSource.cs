using Npgsql;

namespace Waybill.EntityFrameworkCore.Dispatching;

/// <summary>The dispatcher's own data source, owned (and disposed) by the container.</summary>
internal sealed class DispatcherDataSource(NpgsqlDataSource value) : IAsyncDisposable, IDisposable
{
    public NpgsqlDataSource Value { get; } = value;

    /// <summary>
    /// A data source whose sessions run without JIT. With a backlog, the claim's estimated cost passes
    /// <c>jit_above_cost</c> and PostgreSQL would compile it on every run, about half a second per claim with 200
    /// thousand messages waiting. Put first, so options in the application's connection string still win.
    /// </summary>
    public static DispatcherDataSource Create(string connectionString)
    {
        var builder = new NpgsqlConnectionStringBuilder(connectionString);
        builder.Options = string.IsNullOrWhiteSpace(builder.Options) ? "-c jit=off" : "-c jit=off " + builder.Options;
        return new DispatcherDataSource(NpgsqlDataSource.Create(builder.ConnectionString));
    }

    public ValueTask DisposeAsync() => Value.DisposeAsync();

    public void Dispose() => Value.Dispose();
}
