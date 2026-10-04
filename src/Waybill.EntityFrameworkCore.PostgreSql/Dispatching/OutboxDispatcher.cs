using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Waybill.EntityFrameworkCore.Dispatching;

/// <summary>What one dispatcher cycle did.</summary>
/// <param name="Claimed">Rows claimed.</param>
/// <param name="TransportFailed">The transport failed for the whole batch (exception, timeout or invalid result).</param>
internal readonly record struct DispatchCycle(int Claimed, bool TransportFailed);

/// <summary>One dispatcher instance: claims a batch, publishes it outside any transaction, and records the outcome.</summary>
internal sealed partial class OutboxDispatcher(
    OutboxStore store,
    ITransport transport,
    IOptions<WaybillOptions> waybillOptions,
    IOptions<WaybillDispatcherOptions> dispatcherOptions,
    ILogger<OutboxDispatcher> logger)
{
    /// <summary>Unique per process incarnation: a restarted or zombie process never shares it.</summary>
    public string Owner { get; } = $"{Environment.MachineName}/{Environment.ProcessId}/{Guid.NewGuid().ToString("N")[..8]}";

    /// <summary>Runs one cycle.</summary>
    public async Task<DispatchCycle> RunOnceAsync(CancellationToken cancellationToken)
    {
        var options = dispatcherOptions.Value;
        var claimed = await store.ClaimAsync(Owner, options.BatchSize, options.Lease, cancellationToken).ConfigureAwait(false);
        if (claimed.Count == 0)
            return new DispatchCycle(0, TransportFailed: false);

        // A row written under a larger limit than the current one goes to the DLQ without touching the broker.
        var maxPayloadBytes = waybillOptions.Value.MaxPayloadBytes;
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

        var transportFailed = false;
        if (toPublish.Count > 0)
        {
            var results = await PublishAsync(toPublish.Select(i => claimed[i].Message).ToList(), options.PublishTimeout).ConfigureAwait(false);
            transportFailed = results is null;
            for (var j = 0; j < toPublish.Count; j++)
                outcomes[toPublish[j]] = (claimed[toPublish[j]], results?[j] ?? PublishResult.Retry);
        }

        // Recording the outcome is not cancelled by shutdown: the batch was published, its rows must say so.
        var finish = await store.FinishAsync(Owner, outcomes, options.MaxReturns, CancellationToken.None).ConfigureAwait(false);
        if (finish.Fenced > 0)
            LogFenced(logger, finish.Fenced);
        foreach (var (id, reason) in finish.DeadLettered)
            LogDeadLettered(logger, id, reason);

        return new DispatchCycle(claimed.Count, transportFailed);
    }

    // The publish wait ends at the timeout even if the transport ignores the token (a hung socket): the batch is then
    // handed back, and the abandoned call is left to finish or fail on its own. Not linked to shutdown: the batch in
    // flight finishes, bounded by the timeout. Returns null when the outcome is unknown for the whole batch.
    private async Task<IReadOnlyList<PublishResult>?> PublishAsync(List<OutgoingMessage> batch, TimeSpan timeout)
    {
        using var timeoutSource = new CancellationTokenSource(timeout);
        Task<IReadOnlyList<PublishResult>>? call = null;
        try
        {
            call = transport.PublishAsync(batch, timeoutSource.Token);
            var results = await call.WaitAsync(timeout).ConfigureAwait(false);
            if (results.Count != batch.Count)
                throw new InvalidOperationException($"The transport returned {results.Count} results for a batch of {batch.Count}.");

            // A result outside the enum (default(PublishResult) included) must never count as confirmed.
            var invalid = results.Count(r => !Enum.IsDefined(r.Status));
            if (invalid > 0)
            {
                LogInvalidResults(logger, invalid);
                return results.Select(r => Enum.IsDefined(r.Status) ? r : PublishResult.Retry).ToList();
            }

            foreach (var retry in results.Where(r => r.Status == PublishStatus.Retry && r.Reason is not null).Take(1))
                LogRetry(logger, retry.Reason!);
            return results;
        }
        catch (Exception exception)
        {
            var timedOut = exception is TimeoutException || timeoutSource.IsCancellationRequested;
            LogPublishFailed(logger, exception, batch.Count, timedOut);
            if (call is { IsCompleted: false })
                _ = call.ContinueWith(static t => _ = t.Exception, TaskScheduler.Default); // observe the abandoned call
            return null;
        }
    }

    public Task<int> ReleaseOwnedAsync(CancellationToken cancellationToken) => store.ReleaseOwnedAsync(Owner, cancellationToken);

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

    [LoggerMessage(EventId = 14, Level = LogLevel.Information, Message = "The transport asked to publish again: {Reason}")]
    private static partial void LogRetry(ILogger logger, string reason);
}
