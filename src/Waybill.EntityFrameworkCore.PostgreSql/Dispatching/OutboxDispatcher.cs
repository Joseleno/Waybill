using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Waybill.EntityFrameworkCore.Dispatching;

/// <summary>How a dispatcher cycle ended.</summary>
internal enum DispatchOutcome
{
    /// <summary>Nothing to claim.</summary>
    Idle,

    /// <summary>The batch was published (or its messages finished as returned or defective).</summary>
    Progress,

    /// <summary>Confirmation timeout or nack: the batch was handed back and the batch size halved.</summary>
    Pressure,

    /// <summary>Connection or channel failure: the batch was handed back and the breaker opened.</summary>
    ConnectionFailure,

    /// <summary>The breaker is open: nothing was claimed.</summary>
    BreakerOpen,
}

/// <summary>What one dispatcher cycle did.</summary>
/// <param name="Claimed">Rows claimed.</param>
/// <param name="Outcome">How the cycle ended.</param>
/// <param name="BatchSize">The batch size this cycle used.</param>
internal readonly record struct DispatchCycle(int Claimed, DispatchOutcome Outcome, int BatchSize)
{
    public bool TransportFailed => Outcome is DispatchOutcome.ConnectionFailure or DispatchOutcome.Pressure;
}

/// <summary>One dispatcher instance: claims a batch, publishes it outside any transaction, and records the outcome.</summary>
internal sealed partial class OutboxDispatcher
{
    private readonly OutboxStore _store;
    private readonly ITransport _transport;
    private readonly IOptions<WaybillOptions> _waybillOptions;
    private readonly WaybillDispatcherOptions _options;
    private readonly ILogger<OutboxDispatcher> _logger;
    private readonly CircuitBreaker _breaker;
    private readonly BatchSizer _batchSizer;
    private readonly PartitionStore? _partitions;
    private readonly bool _ordered;

    public OutboxDispatcher(
        OutboxStore store,
        ITransport transport,
        IOptions<WaybillOptions> waybillOptions,
        IOptions<WaybillDispatcherOptions> dispatcherOptions,
        ILogger<OutboxDispatcher> logger,
        TimeProvider? timeProvider = null,
        PartitionStore? partitions = null)
    {
        _store = store;
        _partitions = partitions;
        _ordered = waybillOptions.Value.OrderByKey;
        _transport = transport;
        _waybillOptions = waybillOptions;
        _options = dispatcherOptions.Value;
        _logger = logger;
        _breaker = new CircuitBreaker(timeProvider ?? TimeProvider.System, _options.PollingInterval, WaybillDispatcherService.MaxBackoff);
        _batchSizer = new BatchSizer(_options.BatchSize);
    }

    /// <summary>Unique per process incarnation: a restarted or zombie process never shares it.</summary>
    public string Owner { get; } = $"{Environment.MachineName}/{Environment.ProcessId}/{Guid.NewGuid().ToString("N")[..8]}";

    /// <summary>How long the breaker stays open; zero when closed or half-open.</summary>
    public TimeSpan BreakerRemaining => _breaker.Remaining;

    internal int CurrentBatchSize => _batchSizer.Current;

    /// <summary>Whether this instance orders by key, and so holds partitions it must renew.</summary>
    public bool OrdersByKey => _ordered;

    /// <summary>With <c>OrderByKey</c>: the partitions this instance held after its last upkeep, with their epochs.</summary>
    public IReadOnlyDictionary<int, long> HeldPartitions { get; private set; } = new SortedDictionary<int, long>();

    private OrderingSettings Ordering => new(_options.Partitions, _options.PartitionLease);

    private PartitionStore Partitions => _partitions ?? throw new InvalidOperationException("Ordering by key needs the partition store.");

    /// <summary>
    /// Checks that this instance agrees with the others on ordering (ADR 0007): with <c>OrderByKey</c>, the stored P and
    /// partition lease (created by the first instance, at startup); without it, that no instance orders. A disagreement
    /// is a configuration error no retry fixes.
    /// </summary>
    /// <returns>What is wrong and how to fix it, or null when this instance may claim.</returns>
    public async Task<string?> CheckOrderingAsync(bool atStartup, CancellationToken cancellationToken)
    {
        if (_partitions is null)
            return null;

        if (!_ordered)
        {
            return await _partitions.ReadSettingsAsync(cancellationToken).ConfigureAwait(false) is null
                ? null
                : "another dispatcher orders by key (waybill.settings exists) and this one does not, so it would claim keyed rows "
                    + "outside any partition. Set OrderByKey on every instance, or stop them all and turn ordering off as OPERATIONS.md shows";
        }

        var stored = atStartup
            ? await _partitions.EnsureSettingsAsync(Ordering, cancellationToken).ConfigureAwait(false)
            : await _partitions.ReadSettingsAsync(cancellationToken).ConfigureAwait(false);
        if (stored is null)
            return "waybill.settings was removed while this dispatcher orders by key. Stop every dispatcher before turning ordering off";
        return stored == Ordering
            ? null
            : $"the database holds Partitions = {stored.Value.Partitions} and PartitionLease = {stored.Value.PartitionLease}, and this instance "
                + $"has {Ordering.Partitions} and {Ordering.PartitionLease}. Every dispatcher must use the same values; change them with all "
                + "dispatchers stopped, as OPERATIONS.md shows";
    }

    /// <summary>Runs one cycle.</summary>
    public async Task<DispatchCycle> RunOnceAsync(CancellationToken cancellationToken)
    {
        // Partition upkeep runs every cycle, with the breaker open too: a broker outage must not cost the partitions.
        // Nothing is in flight here, so handing back partitions above the fair share is safe.
        if (_ordered)
            HeldPartitions = await Partitions.MaintainAsync(Owner, Ordering, cancellationToken).ConfigureAwait(false);

        if (_breaker.IsOpen)
            return new DispatchCycle(0, DispatchOutcome.BreakerOpen, 0);

        var batchSize = _breaker.IsHalfOpen ? 1 : _batchSizer.Current; // half-open: probe with a single message
        var claimed = await _store.ClaimAsync(
            Owner, batchSize, _options.Lease, cancellationToken, _ordered ? _options.Partitions : null).ConfigureAwait(false);
        if (claimed.Count == 0)
            return new DispatchCycle(0, DispatchOutcome.Idle, batchSize);

        // A row written under a larger limit than the current one goes to the DLQ without touching the broker.
        var maxPayloadBytes = _waybillOptions.Value.MaxPayloadBytes;
        var outcomes = new (ClaimedMessage Claim, PublishResult Result)[claimed.Count];
        var toPublish = new List<int>(claimed.Count);
        for (var i = 0; i < claimed.Count; i++)
        {
            if (claimed[i].Message.Payload.Length > maxPayloadBytes)
                outcomes[i] = (claimed[i], PublishResult.Defect(
                    $"payload of {claimed[i].Message.Payload.Length} bytes is above MaxPayloadBytes ({maxPayloadBytes})"));
            else
                toPublish.Add(i);
        }

        IReadOnlyList<PublishResult> results = [];
        if (toPublish.Count > 0)
        {
            results = await PublishAsync(toPublish.Select(i => claimed[i].Message).ToList()).ConfigureAwait(false);
            for (var j = 0; j < toPublish.Count; j++)
                outcomes[toPublish[j]] = (claimed[toPublish[j]], results[j]);
        }

        // Recording the outcome is not cancelled by shutdown: the batch was published, its rows must say so.
        var finish = await _store.FinishAsync(Owner, outcomes, ReturnPolicy.From(_options), CancellationToken.None).ConfigureAwait(false);
        if (finish.Fenced > 0)
            LogFenced(_logger, finish.Fenced);
        foreach (var (id, reason) in finish.DeadLettered)
            LogDeadLettered(_logger, id, reason);

        return new DispatchCycle(claimed.Count, React(results), batchSize);
    }

    /// <summary>Confirmation timeouts in a row, already at a batch of one, after which the broker counts as unreachable.</summary>
    internal const int SilentOutageThreshold = 3;

    private int _pressureAtOne;

    // ADR 0003: only a connection or channel failure opens the breaker; a confirmation timeout, a nack or an
    // unstated cause halves the batch instead, so a slow broker never makes the breaker oscillate. A cycle that never
    // reached the broker (only local defects) says nothing about it and changes neither.
    private DispatchOutcome React(IReadOnlyList<PublishResult> results)
    {
        if (results.Count == 0)
            return DispatchOutcome.Progress;

        var retries = results.Where(r => r.Status == PublishStatus.Retry).ToList();
        if (retries.Any(r => r.Failure == TransportFailure.Connection))
            return OpenBreaker();

        if (retries.Count > 0)
        {
            // The probe failed, by timeout or nack rather than by connection: still a failed probe, so reopen for
            // twice as long instead of closing and cycling the backlog through claims at the base period.
            if (_breaker.IsHalfOpen)
                return OpenBreaker();

            // A network that silently drops packets never raises a connection error: every publish just times out.
            // Once the batch is down to one and keeps timing out, treat it as the outage it is.
            _pressureAtOne = _batchSizer.Current == 1 ? _pressureAtOne + 1 : 0;
            if (_pressureAtOne >= SilentOutageThreshold)
                return OpenBreaker();

            _batchSizer.OnPressure();
            LogBatchReduced(_logger, _batchSizer.Current);
            return DispatchOutcome.Pressure;
        }

        _pressureAtOne = 0;
        _breaker.RecordSuccess();
        _batchSizer.OnHealthy();
        return DispatchOutcome.Progress;
    }

    private DispatchOutcome OpenBreaker()
    {
        _pressureAtOne = 0;
        _breaker.RecordConnectionFailure();
        LogBreakerOpened(_logger, _breaker.Remaining);
        return DispatchOutcome.ConnectionFailure;
    }

    // The publish wait ends at the timeout even if the transport ignores the token (a hung socket): the batch is then
    // handed back, and the abandoned call is left to finish or fail on its own. Not linked to shutdown: the batch in
    // flight finishes, bounded by the timeout. A whole-batch failure becomes one Retry per message, with its cause.
    private async Task<IReadOnlyList<PublishResult>> PublishAsync(List<OutgoingMessage> batch)
    {
        var timeout = _options.PublishTimeout;
        using var timeoutSource = new CancellationTokenSource(timeout);
        Task<IReadOnlyList<PublishResult>>? call = null;
        try
        {
            call = _transport.PublishAsync(batch, timeoutSource.Token);
            var results = await call.WaitAsync(timeout).ConfigureAwait(false);
            if (results.Count != batch.Count)
                throw new InvalidOperationException($"The transport returned {results.Count} results for a batch of {batch.Count}.");

            // A result outside the enum (default(PublishResult) included) must never count as confirmed.
            var invalid = results.Count(r => !Enum.IsDefined(r.Status));
            if (invalid > 0)
            {
                LogInvalidResults(_logger, invalid);
                return results.Select(r => Enum.IsDefined(r.Status) ? r : PublishResult.Retry).ToList();
            }

            foreach (var retry in results.Where(r => r.Status == PublishStatus.Retry && r.Reason is not null).Take(1))
                LogRetry(_logger, retry.Failure, retry.Reason!);
            return results;
        }
        catch (Exception exception)
        {
            var timedOut = exception is TimeoutException or OperationCanceledException || timeoutSource.IsCancellationRequested;
            LogPublishFailed(_logger, exception, batch.Count, timedOut);
            if (call is { IsCompleted: false })
                _ = call.ContinueWith(static t => _ = t.Exception, TaskScheduler.Default); // observe the abandoned call
            var failure = timedOut ? TransportFailure.ConfirmTimeout : TransportFailure.Connection;
            return Enumerable.Repeat(PublishResult.RetryAfter(failure, exception.Message), batch.Count).ToList();
        }
    }

    /// <summary>Hands back the rows this instance holds, then its partitions, so the others take them without waiting.</summary>
    public async Task<int> ReleaseOwnedAsync(CancellationToken cancellationToken)
    {
        var released = await _store.ReleaseOwnedAsync(Owner, cancellationToken).ConfigureAwait(false);
        if (_ordered)
            await Partitions.LeaveAsync(Owner, cancellationToken).ConfigureAwait(false);
        return released;
    }

    public Task<string> DefaultIsolationAsync(CancellationToken cancellationToken) => _store.DefaultIsolationAsync(cancellationToken);

    [LoggerMessage(EventId = 10, Level = LogLevel.Warning,
        Message = "Publishing a batch of {Count} message(s) failed (timed out: {TimedOut}); the batch is handed back to be published again.")]
    private static partial void LogPublishFailed(ILogger logger, Exception exception, int count, bool timedOut);

    [LoggerMessage(EventId = 11, Level = LogLevel.Warning,
        Message = "{Count} outbox row(s) were fenced off: their lease expired and another claim took them. The newer claim decides their outcome.")]
    private static partial void LogFenced(ILogger logger, int count);

    [LoggerMessage(EventId = 12, Level = LogLevel.Error, Message = "Message {MessageId} went to the outbox DLQ: {Reason}")]
    private static partial void LogDeadLettered(ILogger logger, Guid messageId, string reason);

    [LoggerMessage(EventId = 13, Level = LogLevel.Error,
        Message = "The transport returned {Count} result(s) with an undefined status; they are treated as Retry, never as confirmed.")]
    private static partial void LogInvalidResults(ILogger logger, int count);

    [LoggerMessage(EventId = 14, Level = LogLevel.Information, Message = "The transport asked to publish again ({Failure}): {Reason}")]
    private static partial void LogRetry(ILogger logger, TransportFailure failure, string reason);

    [LoggerMessage(EventId = 15, Level = LogLevel.Warning,
        Message = "Connection or channel failure: the circuit breaker is open for {Duration}; nothing is claimed meanwhile.")]
    private static partial void LogBreakerOpened(ILogger logger, TimeSpan duration);

    [LoggerMessage(EventId = 16, Level = LogLevel.Warning,
        Message = "The broker is slow or pushing back: the batch size is now {BatchSize}. The breaker stays closed.")]
    private static partial void LogBatchReduced(ILogger logger, int batchSize);
}
