namespace Waybill.EntityFrameworkCore.Dispatching;

/// <summary>What the dispatcher loop last did, written by the hosted service and read by the health check.</summary>
internal sealed class DispatcherStatus(TimeProvider time)
{
    private readonly Lock _lock = new();
    private Snapshot _current = new(false, null, null, null, 0);

    /// <summary>A consistent view of the loop's state.</summary>
    /// <param name="Running">The loop is between start and stop.</param>
    /// <param name="StartedAt">When the loop started; null if it never did.</param>
    /// <param name="LastCycleAt">When the last cycle ended, successfully or not.</param>
    /// <param name="LastOutcome">How the last successful cycle ended.</param>
    /// <param name="ConsecutiveDatabaseFailures">Cycles in a row that failed against the database.</param>
    public readonly record struct Snapshot(
        bool Running, DateTimeOffset? StartedAt, DateTimeOffset? LastCycleAt, DispatchOutcome? LastOutcome, int ConsecutiveDatabaseFailures);

    public Snapshot Current
    {
        get
        {
            lock (_lock)
                return _current;
        }
    }

    public void Started()
    {
        lock (_lock)
            _current = new(true, time.GetUtcNow(), null, null, 0);
    }

    public void Stopped()
    {
        lock (_lock)
            _current = _current with { Running = false };
    }

    public void CycleCompleted(DispatchOutcome outcome)
    {
        lock (_lock)
            _current = _current with { LastCycleAt = time.GetUtcNow(), LastOutcome = outcome, ConsecutiveDatabaseFailures = 0 };
    }

    public void CycleFailed()
    {
        lock (_lock)
            _current = _current with { LastCycleAt = time.GetUtcNow(), ConsecutiveDatabaseFailures = _current.ConsecutiveDatabaseFailures + 1 };
    }
}
