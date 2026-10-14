using Npgsql;

namespace Waybill.Tests.Integration.G4;

/// <summary>
/// The database clock of the sessions that opt in, under the test's control. Their search path puts a
/// <c>clock_timestamp()</c> of the schema <c>waybill_test</c> before <c>pg_catalog</c>, so the dispatcher's unqualified
/// calls reach it; the triggers (<c>SECURITY DEFINER</c> with a fixed search path) still read the real clock.
/// </summary>
/// <remarks>
/// Each read counts, and waits while the test holds the pause. A statement reads its snapshot before it evaluates any
/// expression, so a claim paused at its clock has its snapshot and has locked nothing yet: the test commits a change
/// there and lets go, and the claim meets that change only when it locks the row (EvalPlanQual).
/// </remarks>
public sealed class TestClock : IAsyncDisposable
{
    private const long LockId = 4343;
    private readonly TestDatabase _database;
    private readonly NpgsqlConnection _holder;

    private TestClock(TestDatabase database, NpgsqlConnection holder, NpgsqlDataSource dataSource)
    {
        _database = database;
        _holder = holder;
        DataSource = dataSource;
    }

    /// <summary>Connections whose unqualified <c>clock_timestamp()</c> is this clock.</summary>
    public NpgsqlDataSource DataSource { get; }

    public static async Task<TestClock> InstallAsync(TestDatabase database)
    {
        await database.ExecuteAsync($"""
            CREATE SCHEMA IF NOT EXISTS waybill_test;
            CREATE TABLE IF NOT EXISTS waybill_test.clock (id int PRIMARY KEY, reads bigint NOT NULL);
            INSERT INTO waybill_test.clock VALUES (1, 0) ON CONFLICT (id) DO UPDATE SET reads = 0;
            CREATE OR REPLACE FUNCTION waybill_test.clock_timestamp() RETURNS timestamptz LANGUAGE plpgsql VOLATILE AS $$
            BEGIN
                UPDATE waybill_test.clock SET reads = reads + 1 WHERE id = 1;
                PERFORM pg_advisory_lock_shared({LockId});
                PERFORM pg_advisory_unlock_shared({LockId});
                RETURN pg_catalog.clock_timestamp();
            END $$;
            """);
        var holder = new NpgsqlConnection(database.ConnectionString);
        await holder.OpenAsync();
        var builder = new NpgsqlConnectionStringBuilder(database.ConnectionString) { SearchPath = "waybill_test, pg_catalog, public" };
        return new TestClock(database, holder, NpgsqlDataSource.Create(builder.ConnectionString));
    }

    /// <summary>Every read of this clock from now on waits until <see cref="ResumeAsync"/>.</summary>
    public Task PauseAsync() => KeyLockHarness.ExecuteAsync(_holder, $"SELECT pg_advisory_lock({LockId})");

    public Task ResumeAsync() => KeyLockHarness.ExecuteAsync(_holder, $"SELECT pg_advisory_unlock({LockId})");

    /// <summary>Waits until <paramref name="sessions"/> sessions wait at the paused clock.</summary>
    public Task WaitForPausedAsync(int sessions = 1) => KeyLockHarness.WaitForAsync(_holder, sessions, 0);

    /// <summary>How many times the clock was read since it was installed or last reset.</summary>
    public Task<long> ReadsAsync() => _database.ScalarAsync("SELECT reads FROM waybill_test.clock WHERE id = 1");

    public Task ResetReadsAsync() => _database.ExecuteAsync("UPDATE waybill_test.clock SET reads = 0 WHERE id = 1");

    public async ValueTask DisposeAsync()
    {
        await DataSource.DisposeAsync();
        await _holder.DisposeAsync();
    }
}
