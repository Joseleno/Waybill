using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Waybill.EntityFrameworkCore.Dispatching;

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

    /// <summary>Runs one cycle and returns how many messages were claimed.</summary>
    public async Task<int> RunOnceAsync(CancellationToken cancellationToken)
    {
        var options = dispatcherOptions.Value;
        var claimed = await store.ClaimAsync(Owner, options.BatchSize, options.Lease, cancellationToken).ConfigureAwait(false);
        if (claimed.Count == 0)
            return 0;

        // A row written under a larger limit than the current one goes to the DLQ without touching the broker.
        var maxPayloadBytes = waybillOptions.Value.MaxPayloadBytes;
        var outcomes = new (ClaimedMessage Claim, PublishResult Result)[claimed.Count];
        var toPublish = new List<int>(claimed.Count);
        for (var i = 0; i < claimed.Count; i++)
        {
            if (claimed[i].Message.Payload.Length > maxPayloadBytes)
                outcomes[i] = (claimed[i], new PublishResult(PublishStatus.Defect,
                    $"payload of {claimed[i].Message.Payload.Length} bytes is above MaxPayloadBytes ({maxPayloadBytes})"));
            else
                toPublish.Add(i);
        }

        if (toPublish.Count > 0)
        {
            var results = await PublishAsync(toPublish.Select(i => claimed[i].Message).ToList(), options.PublishTimeout).ConfigureAwait(false);
            for (var j = 0; j < toPublish.Count; j++)
                outcomes[toPublish[j]] = (claimed[toPublish[j]], results[j]);
        }

        // Recording the outcome is not cancelled by shutdown: the batch was published, its rows must say so.
        var fenced = await store.FinishAsync(Owner, outcomes, options.MaxReturns, CancellationToken.None).ConfigureAwait(false);
        if (fenced > 0)
            LogFenced(logger, fenced);
        foreach (var (claim, result) in outcomes.Where(o => o.Result.Status == PublishStatus.Defect))
            LogDeadLettered(logger, claim.Message.MessageId, claim.Message.Name, result.Reason);

        return claimed.Count;
    }

    // Not linked to shutdown: the batch in flight finishes, bounded by the publish timeout. An exception or the timeout
    // means the outcome is unknown, so every message is handed back (a duplicate is possible, a loss is not).
    private async Task<IReadOnlyList<PublishResult>> PublishAsync(List<OutgoingMessage> batch, TimeSpan timeout)
    {
        using var timeoutSource = new CancellationTokenSource(timeout);
        try
        {
            var results = await transport.PublishAsync(batch, timeoutSource.Token).ConfigureAwait(false);
            if (results.Count != batch.Count)
                throw new InvalidOperationException($"The transport returned {results.Count} results for a batch of {batch.Count}.");
            return results;
        }
        catch (Exception exception)
        {
            LogPublishFailed(logger, exception, batch.Count, timeoutSource.IsCancellationRequested);
            return Enumerable.Repeat(PublishResult.Retry, batch.Count).ToList();
        }
    }

    public Task<int> ReleaseOwnedAsync(CancellationToken cancellationToken) => store.ReleaseOwnedAsync(Owner, cancellationToken);

    [LoggerMessage(EventId = 10, Level = LogLevel.Warning,
        Message = "Publishing a batch of {Count} message(s) failed (timed out: {TimedOut}); the batch is handed back to be published again.")]
    private static partial void LogPublishFailed(ILogger logger, Exception exception, int count, bool timedOut);

    [LoggerMessage(EventId = 11, Level = LogLevel.Warning,
        Message = "{Count} outbox row(s) were fenced off: their lease expired and another claim took them. The newer claim decides their outcome.")]
    private static partial void LogFenced(ILogger logger, int count);

    [LoggerMessage(EventId = 12, Level = LogLevel.Error,
        Message = "Message {MessageId} ({Name}) went to the outbox DLQ: {Reason}")]
    private static partial void LogDeadLettered(ILogger logger, Guid messageId, string name, string? reason);
}
