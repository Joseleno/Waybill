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

    public OutboxDispatcher(
        OutboxStore store,
        ITransport transport,
        IOptions<WaybillOptions> waybillOptions,
        IOptions<WaybillDispatcherOptions> dispatcherOptions,
        ILogger<OutboxDispatcher> logger,
        TimeProvider? timeProvider = null)
    {
        _store = store;
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

    /// <summary>Runs one cycle.</summary>
    public async Task<DispatchCycle> RunOnceAsync(CancellationToken cancellationToken)
    {
        if (_breaker.IsOpen)
            return new DispatchCycle(0, DispatchOutcome.BreakerOpen, 0);

        var batchSize = _breaker.IsHalfOpen ? 1 : _batchSizer.Current; // half-open: probe with a single message
        var claimed = await _store.ClaimAsync(Owner, batchSize, _options.Lease, cancellationToken).ConfigureAwait(false);
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
        var finish = await _store.FinishAsync(Owner, outcomes, _options.MaxReturns, CancellationToken.None).ConfigureAwait(false);
        if (finish.Fenced > 0)
            LogFenced(_logger, finish.Fenced);
        foreach (var (id, reason) in finish.DeadLettered)
            LogDeadLettered(_logger, id, reason);

        return new DispatchCycle(claimed.Count, React(results), batchSize);
    }

    // ADR 0003: only a connection or channel failure opens the breaker; a confirmation timeout or a nack halves the
    // batch instead, so a slow broker never makes the breaker oscillate.
    private DispatchOutcome React(IReadOnlyList<PublishResult> results)
    {
        var retries = results.Where(r => r.Status == PublishStatus.Retry).ToList();
        if (retries.Any(r => r.Failure is TransportFailure.Connection or TransportFailure.Unspecified))
        {
            _breaker.RecordConnectionFailure();
            LogBreakerOpened(_logger, _breaker.Remaining);
            return DispatchOutcome.ConnectionFailure;
        }

        _breaker.RecordSuccess();
        if (retries.Count > 0)
        {
            _batchSizer.OnPressure();
            LogBatchReduced(_logger, _batchSizer.Current);
            return DispatchOutcome.Pressure;
        }

        _batchSizer.OnHealthy();
        return DispatchOutcome.Progress;
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

    public Task<int> ReleaseOwnedAsync(CancellationToken cancellationToken) => _store.ReleaseOwnedAsync(Owner, cancellationToken);

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
