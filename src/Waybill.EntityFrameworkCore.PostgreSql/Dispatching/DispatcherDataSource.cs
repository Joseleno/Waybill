using Npgsql;

namespace Waybill.EntityFrameworkCore.Dispatching;

/// <summary>The dispatcher's own data source, owned (and disposed) by the container.</summary>
internal sealed class DispatcherDataSource(NpgsqlDataSource value) : IAsyncDisposable, IDisposable
{
    public NpgsqlDataSource Value { get; } = value;

    /// <summary>
    /// A data source whose sessions run without JIT. With a backlog, the claim's estimated cost passes
    /// <c>jit_above_cost</c> and PostgreSQL would compile it on every run, about half a second per claim with 200
    /// thousand messages waiting. Set on each new physical connection, not as the startup parameter <c>options</c>: a
    /// pooler such as PgBouncer refuses a startup parameter it does not track. Behind a pooler in transaction mode the
    /// setting does not follow the transaction to the server; set <c>jit = off</c> for the dispatcher's role there.
    /// </summary>
    public static DispatcherDataSource Create(string connectionString)
    {
        const string NoJit = "SET jit = off";
        var builder = new NpgsqlDataSourceBuilder(connectionString);
        // The pool's reset of a connection handed back (DISCARD ALL) would undo the setting. The dispatcher keeps no
        // session state of its own (no advisory lock, temporary table or session setting), so nothing needs resetting.
        builder.ConnectionStringBuilder.NoResetOnClose = true;
        builder.UsePhysicalConnectionInitializer(
            connection =>
            {
                using var command = new NpgsqlCommand(NoJit, connection);
                command.ExecuteNonQuery();
            },
            async connection =>
            {
                await using var command = new NpgsqlCommand(NoJit, connection);
                await command.ExecuteNonQueryAsync().ConfigureAwait(false);
            });
        return new DispatcherDataSource(builder.Build());
    }

    public ValueTask DisposeAsync() => Value.DisposeAsync();

    public void Dispose() => Value.Dispose();
}
