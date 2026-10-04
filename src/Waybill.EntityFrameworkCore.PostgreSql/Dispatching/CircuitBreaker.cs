namespace Waybill.EntityFrameworkCore.Dispatching;

/// <summary>
/// Opens only on connection or channel failures (ADR 0003). Open: the dispatcher does not claim at all, so a broker
/// outage leaves the backlog untouched instead of cycling it through claims. After the open period it is half-open:
/// one probe with a batch of one; success closes it, another connection failure reopens it for twice as long.
/// </summary>
internal sealed class CircuitBreaker(TimeProvider time, TimeSpan baseDelay, TimeSpan maxDelay)
{
    private int _consecutiveFailures;
    private DateTimeOffset _openUntil;

    public bool IsOpen => _consecutiveFailures > 0 && time.GetUtcNow() < _openUntil;

    public bool IsHalfOpen => _consecutiveFailures > 0 && !IsOpen;

    public TimeSpan Remaining => IsOpen ? _openUntil - time.GetUtcNow() : TimeSpan.Zero;

    public void RecordConnectionFailure()
    {
        _consecutiveFailures++;
        _openUntil = time.GetUtcNow() + Delay(baseDelay, maxDelay, _consecutiveFailures);
    }

    public void RecordSuccess() => _consecutiveFailures = 0;

    internal static TimeSpan Delay(TimeSpan baseDelay, TimeSpan maxDelay, int consecutiveFailures)
    {
        var factor = Math.Pow(2, Math.Min(Math.Max(consecutiveFailures - 1, 0), 16));
        return TimeSpan.FromTicks((long)Math.Min(baseDelay.Ticks * factor, Math.Max(maxDelay.Ticks, baseDelay.Ticks)));
    }
}

/// <summary>
/// The batch size in use. A confirmation timeout or a nack (back-pressure) halves it, down to one, without opening
/// the breaker; each healthy batch doubles it back toward the configured size.
/// </summary>
internal sealed class BatchSizer
{
    private readonly int _max;

    public BatchSizer(int max)
    {
        _max = max;
        Current = max;
    }

    public int Current { get; private set; }

    public void OnPressure() => Current = Math.Max(1, Current / 2);

    public void OnHealthy() => Current = Math.Min(_max, Current * 2);
}
