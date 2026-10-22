using System.Diagnostics;
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
/// <param name="MoreOfKeys">
/// Ordering by key: a key of the batch, not stopped for good, has rows claimable now: rows waiting behind its last claimed
/// one, or claimed rows this batch did not send.
/// </param>
internal readonly record struct DispatchCycle(int Claimed, DispatchOutcome Outcome, int BatchSize, bool MoreOfKeys = false)
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
    private readonly TimeProvider _time;
    private readonly OutboxMetrics? _metrics;
    private long? _lastUpkeep; // TimeProvider timestamp of the last partition upkeep that succeeded
    private int _maxPerKey; // the effective M: MaxPerKey, brought down when rounds run out of time

    public OutboxDispatcher(
        OutboxStore store,
        ITransport transport,
        IOptions<WaybillOptions> waybillOptions,
        IOptions<WaybillDispatcherOptions> dispatcherOptions,
        ILogger<OutboxDispatcher> logger,
        TimeProvider? timeProvider = null,
        PartitionStore? partitions = null,
        OutboxMetrics? metrics = null)
    {
        _store = store;
        _partitions = partitions;
        _metrics = metrics;
        _ordered = dispatcherOptions.Value.OrderByKey;
        _transport = transport;
        _waybillOptions = waybillOptions;
        _options = dispatcherOptions.Value;
        _logger = logger;
        _time = timeProvider ?? TimeProvider.System;
        _breaker = new CircuitBreaker(timeProvider ?? TimeProvider.System, _options.PollingInterval, WaybillDispatcherService.MaxBackoff);
        _batchSizer = new BatchSizer(_options.BatchSize);
        _maxPerKey = _ordered ? Math.Max(1, _options.MaxPerKey) : 1;
    }

    /// <summary>Unique per process incarnation: a restarted or zombie process never shares it.</summary>
    public string Owner { get; } = $"{Environment.MachineName}/{Environment.ProcessId}/{Guid.NewGuid().ToString("N")[..8]}";

    /// <summary>How long the breaker stays open; zero when closed or half-open.</summary>
    public TimeSpan BreakerRemaining => _breaker.Remaining;

    internal int CurrentBatchSize => _batchSizer.Current;

    /// <summary>The effective M (ordering by key): <c>MaxPerKey</c>, or less while rounds run out of time.</summary>
    internal int CurrentMaxPerKey => _maxPerKey;

    /// <summary>Whether this instance orders by key, and so holds partitions it must renew.</summary>
    public bool OrdersByKey => _ordered;

    /// <summary>With <c>OrderByKey</c>: the partitions this instance held after its last upkeep, with their epochs.</summary>
    public IReadOnlyDictionary<int, long> HeldPartitions { get; private set; } = new SortedDictionary<int, long>();

    private OrderingSettings Ordering => new(_options.Partitions, _options.PartitionLease);

    // Upkeep is several round trips, too many for every full batch of a backlog. Every (PartitionLease − lease) / 4 is
    // enough: a partition renewed then is still held for longer than a row lease when the claim checks it, three
    // intervals later. Without partitions (just started, or all lost) the instance has nothing to claim, so it upkeeps.
    private bool UpkeepDue() =>
        _lastUpkeep is null || HeldPartitions.Count == 0
        || _time.GetElapsedTime(_lastUpkeep.Value) >= (_options.PartitionLease - _options.Lease) / 4;

    private PartitionStore Partitions => _partitions ?? throw new InvalidOperationException("Ordering by key needs the partition store.");

    // The head pointer's safety net (ADR 0008): one slice of keys per partition upkeep, so a full pass over K keys takes
    // K / RepairSlice upkeeps. Each slice is a few index descents per key.
    internal const int RepairSlice = 2_000;
    private string? _repairAfter; // where the next slice starts; null: from the first key

    private async Task RepairHeadsAsync(CancellationToken cancellationToken)
    {
        if (HeldPartitions.Count == 0)
            return;
        var (next, repaired) = await _store.RepairHeadsAsync(
            _repairAfter, RepairSlice, _options.Partitions, [.. HeldPartitions.Keys], cancellationToken).ConfigureAwait(false);
        _repairAfter = next;
        foreach (var (key, sequence) in repaired)
            LogHeadRepaired(_logger, key, sequence);
        _metrics?.RecordHeadsRepaired(repaired.Count);
    }

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
            if (await _partitions.ReadSettingsAsync(cancellationToken).ConfigureAwait(false) is not null)
                return "another dispatcher orders by key (waybill.settings exists) and this one does not, so it would claim keyed rows "
                    + "outside any partition. Set OrderByKey on every instance, or stop them all and turn ordering off as OPERATIONS.md shows";

            // Rows numbered by a transaction still open when ordering was turned off, which the reset could not mark.
            var marked = await _store.MarkNumberedClaimableAsync(cancellationToken).ConfigureAwait(false);
            if (marked > 0)
                LogNumberedMarked(_logger, marked);
            return null;
        }

        var stored = atStartup
            ? await _partitions.EnsureSettingsAsync(Ordering, cancellationToken).ConfigureAwait(false)
            : await _partitions.ReadSettingsAsync(cancellationToken).ConfigureAwait(false);
        if (stored is null)
            return "waybill.settings was removed while this dispatcher orders by key. Stop every dispatcher before turning ordering off";
        if (stored != Ordering)
            return $"the database holds Partitions = {stored.Value.Partitions} and PartitionLease = {stored.Value.PartitionLease}, and this instance "
                + $"has {Ordering.Partitions} and {Ordering.PartitionLease}. Every dispatcher must use the same values; change them with all "
                + "dispatchers stopped, as OPERATIONS.md shows";

        // Without the numbering trigger, keyed messages are written unnumbered and would go out in any order; without the
        // head pointer's, a key's next message is never marked claimable and the key stops (ADR 0008).
        var off = await _partitions.OrderingTriggersOffAsync(cancellationToken).ConfigureAwait(false);
        if (off.Count > 0)
            return $"the trigger(s) {string.Join(", ", off.Select(t => "waybill." + t))}, which ordering by key relies on, are missing or "
                + "disabled. Run WaybillSchema.MigrateAsync, or " + string.Join("; ", off.Select(t => $"ALTER TABLE waybill.outbox ENABLE TRIGGER {t}"));

        // The dispatcher's own marking must fire the promotion of a key's next message.
        var role = await _partitions.SessionReplicationRoleAsync(cancellationToken).ConfigureAwait(false);
        return role == "replica"
            ? "session_replication_role is 'replica' for this dispatcher's sessions, so publishing a message would not mark the next "
                + "message of its key claimable. Set it back to 'origin' for the dispatcher's role or connection string"
            : null;
    }

    /// <summary>Runs one cycle.</summary>
    public async Task<DispatchCycle> RunOnceAsync(CancellationToken cancellationToken)
    {
        // Partition upkeep runs every cycle, with the breaker open too: a broker outage must not cost the partitions.
        // Nothing is in flight here, so handing back partitions above the fair share is safe.
        if (_ordered && UpkeepDue())
        {
            HeldPartitions = await Partitions.MaintainAsync(Owner, Ordering, cancellationToken).ConfigureAwait(false);
            _lastUpkeep = _time.GetTimestamp();
            try
            {
                await RepairHeadsAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // The sweep is a safety net: a failure of its own must not stop the claim, nor back the dispatcher off as a
                // database outage would. The next upkeep tries again from the same cursor.
                LogSweepFailed(_logger, exception);
            }
        }

        if (_breaker.IsOpen)
            return new DispatchCycle(0, DispatchOutcome.BreakerOpen, 0);

        var started = _time.GetTimestamp(); // the batch's publish deadline counts from here, before the claim
        var batchSize = _breaker.IsHalfOpen ? 1 : _batchSizer.Current; // half-open: probe with a single message
        var claimed = await _store.ClaimAsync(
            Owner, batchSize, _options.Lease, cancellationToken, _ordered ? _options.Partitions : null,
            _maxPerKey).ConfigureAwait(false);
        if (claimed.Count == 0)
            return new DispatchCycle(0, DispatchOutcome.Idle, batchSize);

        var batch = new Batch(claimed);
        try
        {
            await PublishRoundsAsync(batch, started, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            // Recording the outcome is not cancelled by shutdown, nor skipped when a later round failed: what was
            // published must say so, and what was not goes back as it was. The send spans end after it, whatever happens.
            FinishResult? finish = null;
            Exception? failure = null;
            try
            {
                finish = await FinishAsync(batch).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                failure = exception;
                throw;
            }
            finally
            {
                EndSends(batch, finish, failure);
            }
        }
        AdaptMaxPerKey(batch);

        return new DispatchCycle(claimed.Count, React(batch.React), batchSize, batch.MoreOfKeys);
    }

    /// <summary>The outcome of a claimed batch, built round by round.</summary>
    private sealed class Batch(IReadOnlyList<ClaimedMessage> claimed)
    {
        public IReadOnlyList<ClaimedMessage> Claimed { get; } = claimed;

        /// <summary>What the transport said, or a local defect; a row without an outcome was not sent.</summary>
        public Dictionary<Guid, PublishResult> Outcomes { get; } = [];

        /// <summary>The results the breaker and the batch size react to (ADR 0003).</summary>
        public List<PublishResult> React { get; } = [];

        /// <summary>Keys whose series ends in this batch: a message of theirs was not confirmed.</summary>
        public HashSet<string> Stopped { get; } = new(StringComparer.Ordinal);

        /// <summary>Keys stopped for good in this cycle (returned, defective): nothing more of them can go now.</summary>
        public HashSet<string> Finished { get; } = new(StringComparer.Ordinal);

        /// <summary>A message must be published again: no round starts after this one.</summary>
        public bool Ended { get; set; }

        /// <summary>The send span of each message handed to the transport or judged here, by message id.</summary>
        public Dictionary<Guid, Activity> Sends { get; } = [];

        /// <summary>Rounds that ran; set when the deadline left no time for the next one.</summary>
        public int? CutAfter { get; set; }

        /// <summary>Every round ran.</summary>
        public bool Whole { get; set; }

        public bool MoreOfKeys =>
            Claimed.Where(c => c.Sequence is not null && !Finished.Contains(c.Message.Key!))
                .GroupBy(c => c.Message.Key)
                .Any(g => g.MaxBy(c => c.Rank)!.MoreOfKey
                    || g.Any(c => !Outcomes.ContainsKey(c.Message.MessageId)));

        public void Stop(ClaimedMessage row, bool forGood)
        {
            if (row.Sequence is null)
                return;
            Stopped.Add(row.Message.Key!);
            if (forGood)
                Finished.Add(row.Message.Key!);
        }
    }

    // The outcome FinishAsync records for a row that has none in the batch, because it was never handed to the transport:
    // it goes back as it was, without spending an attempt, and never reaches React.
    private static readonly PublishResult NotSent = PublishResult.RetryAfter(TransportFailure.Unspecified, "not sent in this batch");

    // M ≥ 2 (PLAN.md 8c-2): one call per round; round k carries the k-th message of each key, and only of keys whose
    // message of round k − 1 was confirmed. The first copy of a key's message therefore reaches the broker after the
    // first copy of every earlier one. With M = 1, or without ordering, there is one round: the batch as before.
    private async Task PublishRoundsAsync(Batch batch, long started, CancellationToken cancellationToken)
    {
        var plan = Rounds.Build(batch.Claimed);
        foreach (var rejected in plan.Rejected)
        {
            LogRowRejected(_logger, rejected.Row.Message.MessageId, rejected.Reason);
            batch.Stop(rejected.Row, forGood: false);
        }

        var maxPayloadBytes = _waybillOptions.Value.MaxPayloadBytes;
        bool KeyStopped(ClaimedMessage row) => row.Sequence is not null && batch.Stopped.Contains(row.Message.Key!);
        bool Oversized(ClaimedMessage row) => row.Message.Payload.Length > maxPayloadBytes;

        var previous = TimeSpan.Zero; // how long the last round took
        for (var k = 0; k < plan.Rounds.Count; k++)
        {
            // Shutdown: no round starts after the host asked to stop, the first one included; checked, never thrown, so
            // the marking below records what earlier rounds published.
            if (batch.Ended || cancellationToken.IsCancellationRequested)
                return;
            // A round that sends nothing (its keys stopped earlier in the batch, or only defects) takes no time: it is
            // never cut, so M does not come down for it.
            var remaining = _options.PublishTimeout - _time.GetElapsedTime(started);
            if (k > 0 && plan.Rounds[k].Any(row => !KeyStopped(row) && !Oversized(row)) && !Rounds.MayStart(remaining, previous))
            {
                batch.CutAfter = k;
                return;
            }

            var toPublish = new List<ClaimedMessage>();
            foreach (var row in plan.Rounds[k].Where(row => !KeyStopped(row))) // a stopped key's rows are not sent: no outcome
            {
                if (Oversized(row))
                {
                    // Written under a larger limit than the current one: to the DLQ without touching the broker, in its
                    // own round, so a message behind a failed one never goes to the DLQ before its turn.
                    batch.Outcomes[row.Message.MessageId] = PublishResult.Defect(
                        $"payload of {row.Message.Payload.Length} bytes is above MaxPayloadBytes ({maxPayloadBytes})");
                    batch.Stop(row, forGood: true);
                }
                else
                {
                    toPublish.Add(row);
                }
            }
            OpenSends(batch, plan.Rounds[k], toPublish);
            if (toPublish.Count == 0)
            {
                previous = TimeSpan.Zero;
                continue;
            }

            // Round 1 has the whole PublishTimeout, as a batch always had; a later round, what is left of it.
            var roundStarted = _time.GetTimestamp();
            var results = await PublishAsync(toPublish.Select(r => r.Message).ToList(), k == 0 ? _options.PublishTimeout : remaining).ConfigureAwait(false);

            // Recorded before anything else can throw: a round the broker confirmed must be marked as published.
            for (var j = 0; j < toPublish.Count; j++)
            {
                var (row, result) = (toPublish[j], results[j]);
                batch.Outcomes[row.Message.MessageId] = result;
                if (result.Status != PublishStatus.Confirmed)
                    batch.Stop(row, forGood: result.Status is PublishStatus.Returned or PublishStatus.Defect);
                if (result.Status == PublishStatus.Retry)
                    batch.Ended = true;
            }
            // A later round had only what the earlier ones left of the deadline: its confirmation timeout says nothing
            // about the broker. A nack, a connection failure or anything else does (ADR 0003).
            batch.React.AddRange(k == 0 ? results : results.Where(r => !(r.Status == PublishStatus.Retry && r.Failure == TransportFailure.ConfirmTimeout)));
            previous = _time.GetElapsedTime(roundStarted);
        }
        batch.Whole = !batch.Ended;
    }

    // M follows the deadline, as the batch size follows the broker: rounds that keep running out of time bring it down to
    // the rounds that fit, so successors are not claimed only to be handed back; a batch that runs whole doubles it back.
    private void AdaptMaxPerKey(Batch batch)
    {
        if (!_ordered)
            return;
        if (batch.CutAfter is { } ran)
        {
            var reduced = Math.Max(1, ran);
            if (reduced < _maxPerKey)
                LogMaxPerKeyReduced(_logger, reduced, _options.PublishTimeout);
            _maxPerKey = Math.Min(_maxPerKey, reduced);
        }
        else if (batch.Whole)
        {
            _maxPerKey = Math.Min(_options.MaxPerKey, _maxPerKey * 2);
        }
    }

    private async Task<FinishResult> FinishAsync(Batch batch)
    {
        var outcomes = batch.Claimed
            .Select(c => (c, batch.Outcomes.TryGetValue(c.Message.MessageId, out var result) ? result : NotSent))
            .ToList();
        var finish = await FinishWithRetryAsync(outcomes).ConfigureAwait(false);
        if (finish.Fenced > 0)
            LogFenced(_logger, finish.Fenced);
        foreach (var (id, reason) in finish.DeadLettered)
        {
            // With ordering, the row now stops its key: the log says which, and from which sequence on.
            var row = batch.Claimed.First(c => c.Message.MessageId == id);
            LogDeadLettered(_logger, id, row.Message.Key, row.Sequence, reason);
        }
        return finish;
    }

    // A send span for what this round hands to the transport or judges itself (a payload above the limit), never for a
    // row it holds back: that one was not sent. The message carries the span to the transport: its traceparent becomes
    // the span's, for any transport to propagate, and Waybill's own makes the span current while it publishes. The row
    // in the database keeps the creation context, so a later attempt hangs from it again, in the same trace.
    private void OpenSends(Batch batch, IReadOnlyList<ClaimedMessage> round, List<ClaimedMessage> toPublish)
    {
        if (!WaybillActivity.Source.HasListeners())
            return;
        var handed = toPublish.Select(r => r.Message.MessageId).ToHashSet();
        foreach (var row in round)
        {
            var id = row.Message.MessageId;
            var judged = batch.Outcomes.TryGetValue(id, out var result) && result.Status == PublishStatus.Defect;
            if ((!handed.Contains(id) && !judged) || TryStartSend(row) is not { } send)
                continue;
            batch.Sends[id] = send;
            row.Message.SendActivity = send;
            if (row.Message.Headers is IDictionary<string, string> headers) // the store builds one per claim
                headers["traceparent"] = send.Id!;
        }
    }

    // A listener that throws costs the message its span, never its publication.
    private Activity? TryStartSend(ClaimedMessage row)
    {
        try
        {
            return StartSend(row);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private Activity? StartSend(ClaimedMessage row)
    {
        var message = row.Message;
        message.Headers.TryGetValue("tracestate", out var traceState);
        var parent = message.Headers.TryGetValue("traceparent", out var traceParent)
            && ActivityContext.TryParse(traceParent, traceState, isRemote: true, out var context)
                ? context
                : default; // none, or malformed: a root of its own
        List<KeyValuePair<string, object?>> tags =
        [
            new("messaging.operation.type", "send"),
            new("messaging.operation.name", "send"),
            new("messaging.message.id", message.MessageId.ToString()),
        ];
        if (row.Sequence is { } sequence)
            tags.Add(new("waybill.sequence", sequence));
        if (_waybillOptions.Value.RecordKeyAndTenantInTraces)
        {
            if (message.Key is not null)
                tags.Add(new("waybill.key", message.Key));
            if (message.Headers.TryGetValue("tenant_id", out var tenant))
                tags.Add(new("waybill.tenant_id", tenant));
        }
        var tracing = _transport as ITransportTracing;
        tracing?.AddTags(message, tags);
        var destination = tracing?.Destination(message);
        // Parent and link: the conventions ask only for the link to the creation context; the parent keeps every attempt,
        // hours later too, in the trace of the request that wrote the message (ADR 0011).
        return WaybillActivity.StartIsolated(
            destination is null ? "send" : $"send {destination}", ActivityKind.Producer, parent, tags,
            parent == default ? null : [new ActivityLink(parent)]);
    }

    // Each span ends once, with what the marking recorded: "unknown" when it failed, since its commit may or may not have
    // happened. A listener that throws on one span must neither keep the others open nor fail the cycle.
    private static void EndSends(Batch batch, FinishResult? finish, Exception? failure)
    {
        foreach (var (id, send) in batch.Sends)
        {
            try
            {
                var result = batch.Outcomes.TryGetValue(id, out var outcome) ? outcome : NotSent;
                var (state, error) = SendOutcome(result, failure is null ? finish?.Applied.GetValueOrDefault(id) : null, failure);
                send.SetTag("waybill.send.outcome", state);
                if (error is not null)
                {
                    send.SetTag("error.type", error);
                    send.SetStatus(ActivityStatusCode.Error, failure?.Message ?? result.Reason);
                }
                WaybillActivity.StopIsolated(send);
            }
            catch (Exception)
            {
                // Telemetry only: the outcome is in the database, and the next span still has to end.
            }
        }
    }

    /// <summary>The send span's outcome, from the transport's result and the status the marking left (null: not reached).</summary>
    internal static (string Outcome, string? ErrorType) SendOutcome(PublishResult result, string? applied, Exception? failure) =>
        failure is not null ? ("unknown", failure.GetType().FullName)
        : applied is null ? ("fenced", "fenced")
        : result.Status switch
        {
            PublishStatus.Confirmed => ("published", null),
            PublishStatus.Returned => applied == "dlq" ? ("dlq", "returned") : ("returned", "returned"),
            PublishStatus.Defect => ("dlq", "defect"),
            _ => ("retry", result.Failure switch
            {
                TransportFailure.Connection => "connection",
                TransportFailure.ConfirmTimeout => "confirm_timeout",
                TransportFailure.Nacked => "nacked",
                _ => "unspecified",
            }),
        };

    // The marking runs again when PostgreSQL ends it as a deadlock victim or a serialization failure: every statement is
    // fenced, so what already applied applies once. Two zombies crossing on two keys can deadlock (8c-2 review).
    private async Task<FinishResult> FinishWithRetryAsync(IReadOnlyList<(ClaimedMessage, PublishResult)> outcomes)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await _store.FinishAsync(Owner, outcomes, ReturnPolicy.From(_options), CancellationToken.None).ConfigureAwait(false);
            }
            catch (Npgsql.PostgresException exception) when (attempt < 3 && exception.SqlState is "40P01" or "40001")
            {
                LogFinishRetried(_logger, exception.SqlState);
            }
        }
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
    private async Task<IReadOnlyList<PublishResult>> PublishAsync(List<OutgoingMessage> batch, TimeSpan timeout)
    {
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

    [LoggerMessage(EventId = 12, Level = LogLevel.Error, Message = "Message {MessageId} (key {Key}, sequence {Sequence}) went to the outbox DLQ: {Reason}")]
    private static partial void LogDeadLettered(ILogger logger, Guid messageId, string? key, long? sequence, string reason);

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

    [LoggerMessage(EventId = 17, Level = LogLevel.Error,
        Message = "The first unfinished message of key {Key} (sequence {Sequence}) was not marked claimable, and is now. Waybill never "
            + "leaves it so: something changed the outbox outside it (a disabled trigger, a session in replica mode, a restore, a manual "
            + "UPDATE). See OPERATIONS.md, \"Unmarked heads\".")]
    private static partial void LogHeadRepaired(ILogger logger, string key, long sequence);

    [LoggerMessage(EventId = 18, Level = LogLevel.Information,
        Message = "Ordering by key is off: {Count} message(s) numbered while it was on, by transactions still open when it was turned off, are now claimable.")]
    private static partial void LogNumberedMarked(ILogger logger, int count);

    [LoggerMessage(EventId = 19, Level = LogLevel.Error, Message = "Message {MessageId} goes back unsent: {Reason}")]
    private static partial void LogRowRejected(ILogger logger, Guid messageId, string reason);

    [LoggerMessage(EventId = 27, Level = LogLevel.Warning,
        Message = "Recording a batch's outcome failed with {SqlState} (deadlock or serialization failure) and runs again; its statements are fenced.")]
    private static partial void LogFinishRetried(ILogger logger, string sqlState);

    [LoggerMessage(EventId = 28, Level = LogLevel.Warning,
        Message = "The rounds of a batch ran out of PublishTimeout ({PublishTimeout}): MaxPerKey is down to {MaxPerKey} for now, and doubles back as batches run whole. The broker confirms slowly for the messages of a key.")]
    private static partial void LogMaxPerKeyReduced(ILogger logger, int maxPerKey, TimeSpan publishTimeout);

    [LoggerMessage(EventId = 29, Level = LogLevel.Error,
        Message = "The sweep for unmarked key heads failed; the cycle goes on, and the next partition upkeep tries again.")]
    private static partial void LogSweepFailed(ILogger logger, Exception exception);
}
