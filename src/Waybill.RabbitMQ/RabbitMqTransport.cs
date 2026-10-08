using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;
using RabbitMQ.Client.Exceptions;

namespace Waybill.RabbitMQ;

/// <summary>
/// Publishes outbox messages with publisher confirms, persistent delivery and <c>mandatory</c>: a message counts as
/// published only when the broker confirmed it; an unroutable one comes back as <see cref="PublishStatus.Returned"/>.
/// </summary>
/// <remarks>
/// One connection and one confirm-tracking channel, created on first use and created again when the broker closes
/// them. Automatic recovery of the client library is off: a closed channel fails the batch in flight (handed back
/// by the dispatcher) and the next batch opens a fresh one.
/// </remarks>
internal sealed partial class RabbitMqTransport(IOptions<WaybillRabbitMqOptions> options, ILogger<RabbitMqTransport> logger)
    : ITransport, IAsyncDisposable
{
    internal const string KeyHeader = "waybill-key";
    internal const string SequenceHeader = "waybill-sequence";

    private readonly SemaphoreSlim _gate = new(1, 1);
    private IConnection? _connection;
    private IChannel? _channel;
    private bool _disposed;

    /// <summary>Starts with a connection and a channel already open: lets tests stand in for the client library.</summary>
    internal RabbitMqTransport(
        IOptions<WaybillRabbitMqOptions> options, ILogger<RabbitMqTransport> logger, IConnection connection, IChannel channel)
        : this(options, logger)
    {
        _connection = connection;
        _channel = channel;
    }

    public async Task<IReadOnlyList<PublishResult>> PublishAsync(IReadOnlyList<OutgoingMessage> batch, CancellationToken cancellationToken)
    {
        IChannel channel;
        try
        {
            channel = await GetChannelAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            LogConnectFailed(logger, exception);
            var reason = $"connection failed: {exception.Message}";
            return batch.Select(_ => PublishResult.RetryAfter(TransportFailure.Connection, reason)).ToList();
        }

        // Publishes of one batch run concurrently on the channel; each completes when the broker confirms (or
        // returns, or nacks) that message.
        var exchange = options.Value.Exchange!;
        var attempts = await Task.WhenAll(batch.Select(message => PublishOneAsync(channel, exchange, message, cancellationToken)))
            .ConfigureAwait(false);
        if (!attempts.Any(a => a.ChannelClosedByBroker))
            return attempts.Select(a => a.Result).ToList();

        // The broker closed the channel during the batch (for example a message above its max_message_size). The
        // client cannot say which message did it, and every unconfirmed publish failed with it. Publish those again,
        // one by one, each on a channel of its own: a 406 on that channel can only come from that message, so it is the
        // defect; the others go through.
        LogIsolating(logger, attempts.Count(a => a.ChannelClosedByBroker));
        var results = new PublishResult[batch.Count];
        for (var i = 0; i < batch.Count; i++)
        {
            if (!attempts[i].ChannelClosedByBroker)
                results[i] = attempts[i].Result;
            else if (cancellationToken.IsCancellationRequested)
                results[i] = PublishResult.RetryAfter(TransportFailure.ConfirmTimeout, "the publish timeout ended the isolation");
            else
                results[i] = await PublishAloneAsync(exchange, batch[i], cancellationToken).ConfigureAwait(false);
        }
        return results;
    }

    private async Task<PublishResult> PublishAloneAsync(string exchange, OutgoingMessage message, CancellationToken cancellationToken)
    {
        IChannel? channel = null;
        try
        {
            channel = await OpenDedicatedChannelAsync(cancellationToken).ConfigureAwait(false);
            var alone = await PublishOneAsync(channel, exchange, message, cancellationToken).ConfigureAwait(false);
            return alone.ChannelClosedByBroker
                ? PublishResult.Defect($"the broker closed the channel when this message was published alone: {alone.Result.Reason}")
                : alone.Result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return PublishResult.RetryAfter(TransportFailure.ConfirmTimeout, "the publish timeout ended the isolation");
        }
        catch (Exception exception)
        {
            return PublishResult.RetryAfter(TransportFailure.Connection, exception.Message);
        }
        finally
        {
            await DisposeQuietly(channel).ConfigureAwait(false);
        }
    }

    // A fresh confirm-tracking channel on the shared connection, never the shared channel: no stale close state from a
    // previous message can be blamed on this one.
    private async Task<IChannel> OpenDedicatedChannelAsync(CancellationToken cancellationToken)
    {
        await GetChannelAsync(cancellationToken).ConfigureAwait(false); // reopens the connection if it dropped
        return await _connection!.CreateChannelAsync(
            new CreateChannelOptions(publisherConfirmationsEnabled: true, publisherConfirmationTrackingEnabled: true),
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>One publish: its result, and whether the broker (not the network) closed the channel under it.</summary>
    private readonly record struct Attempt(PublishResult Result, bool ChannelClosedByBroker);

    private async Task<Attempt> PublishOneAsync(IChannel channel, string exchange, OutgoingMessage message, CancellationToken cancellationToken)
    {
        // Building the properties touches no network: if the message cannot be expressed in AMQP, retrying will
        // never help, so it is a defect of the message (DLQ), never a silent endless retry.
        BasicProperties properties;
        try
        {
            properties = Properties(message);
        }
        catch (Exception exception)
        {
            return new Attempt(PublishResult.Defect($"cannot be expressed as an AMQP message: {exception.Message}"), false);
        }

        try
        {
            await channel.BasicPublishAsync(exchange, message.Name, mandatory: true, properties, message.Payload, cancellationToken)
                .ConfigureAwait(false);
            return new Attempt(PublishResult.Confirmed, false);
        }
        catch (PublishReturnException returned)
        {
            return new Attempt(PublishResult.Returned($"{returned.ReplyCode} {returned.ReplyText}"), false);
        }
        catch (PublishException)
        {
            return new Attempt(PublishResult.RetryAfter(TransportFailure.Nacked, "nacked by the broker"), false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new Attempt(PublishResult.RetryAfter(TransportFailure.ConfirmTimeout, "no confirmation before the publish timeout"), false);
        }
        catch (OperationInterruptedException interrupted) when (ClosedByBrokerForTheMessage(interrupted))
        {
            var reason = $"{interrupted.ShutdownReason!.ReplyCode} {interrupted.ShutdownReason.ReplyText}";
            return new Attempt(PublishResult.RetryAfter(TransportFailure.Connection, reason), true);
        }
        catch (Exception exception) when (channel.IsOpen && _connection is { IsOpen: true })
        {
            // The publish failed while the connection and the channel stayed up: not a network problem, so retrying
            // would fail the same way forever and, as the oldest row, block every half-open probe behind it. A defect
            // of this message.
            return new Attempt(PublishResult.Defect($"publish failed with the connection and channel up: {exception.Message}"), false);
        }
        catch (Exception exception)
        {
            // Closed channel or connection: the outcome is unknown, so publish again. Never a defect of the message.
            return new Attempt(PublishResult.RetryAfter(TransportFailure.Connection, exception.Message), false);
        }
    }

    // 406 PRECONDITION_FAILED from the broker on a channel whose connection is still up: the broker refused something
    // about what was published (max_message_size, for one), not the network. Only then is isolation worth trying.
    private bool ClosedByBrokerForTheMessage(OperationInterruptedException interrupted) =>
        interrupted.ShutdownReason is { Initiator: ShutdownInitiator.Peer, ReplyCode: 406 } && _connection is { IsOpen: true };

    internal static BasicProperties Properties(OutgoingMessage message)
    {
        // AMQP short strings: the core already rejects longer names and correlation ids at enqueue time; this guards
        // rows written before that check, or by other means.
        EnsureShortString(message.Name, "message name");
        EnsureShortString(message.ContentType, "content type");
        if (message.Headers.TryGetValue("correlation_id", out var correlation))
            EnsureShortString(correlation, "correlation id");

        var headers = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var name in new[] { "traceparent", "tracestate", "tenant_id" })
        {
            if (message.Headers.TryGetValue(name, out var value))
                headers[name] = value;
        }
        if (message.Key is not null)
            headers[KeyHeader] = message.Key;
        if (message.Sequence is { } sequence)
            headers[SequenceHeader] = sequence.ToString(System.Globalization.CultureInfo.InvariantCulture); // a string, like the others

        return new BasicProperties
        {
            MessageId = message.MessageId.ToString(),
            Type = message.Name,
            ContentType = message.ContentType,
            DeliveryMode = DeliveryModes.Persistent,
            Timestamp = new AmqpTimestamp(message.CreatedAt.ToUnixTimeSeconds()),
            CorrelationId = message.Headers.TryGetValue("correlation_id", out var correlationId) ? correlationId : null,
            Headers = headers,
        };
    }

    private static void EnsureShortString(string value, string what)
    {
        if (System.Text.Encoding.UTF8.GetByteCount(value) > 255)
            throw new InvalidOperationException($"The {what} is longer than 255 UTF-8 bytes (AMQP short string).");
    }

    private async Task<IChannel> GetChannelAsync(CancellationToken cancellationToken)
    {
        if (_channel is { IsOpen: true } open)
            return open;

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_channel is { IsOpen: true } reopened)
                return reopened;

            var settings = options.Value;
            await DisposeQuietly(_channel).ConfigureAwait(false);
            _channel = null;

            if (_connection is not { IsOpen: true })
            {
                await DisposeQuietly(_connection).ConfigureAwait(false);
                _connection = null;
                var factory = new ConnectionFactory { Uri = settings.Uri!, ClientProvidedName = settings.ClientProvidedName };
                settings.ConfigureConnectionFactory?.Invoke(factory);
                // Last, so the callback cannot turn it back on: closed connections and channels are replaced here.
                factory.AutomaticRecoveryEnabled = false;
                _connection = await factory.CreateConnectionAsync(cancellationToken).ConfigureAwait(false);
            }

            var channel = await _connection.CreateChannelAsync(
                new CreateChannelOptions(publisherConfirmationsEnabled: true, publisherConfirmationTrackingEnabled: true),
                cancellationToken).ConfigureAwait(false);
            try
            {
                // The exchange is the application's topology. A missing one would close the channel on every
                // publish; say so plainly instead.
                if (settings.Exchange!.Length > 0)
                    await channel.ExchangeDeclarePassiveAsync(settings.Exchange, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationInterruptedException missing)
            {
                await DisposeQuietly(channel).ConfigureAwait(false);
                LogExchangeMissing(logger, settings.Exchange!);
                throw new InvalidOperationException(
                    $"Exchange '{settings.Exchange}' does not exist. Waybill does not create topology: declare it in the application or its infrastructure.",
                    missing);
            }
            catch
            {
                await DisposeQuietly(channel).ConfigureAwait(false); // timeout or I/O error: never leak the channel
                throw;
            }

            _channel = channel;
            return channel;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        // Under the gate, so a channel being created right now is either finished first or never created.
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            _disposed = true;
            await DisposeQuietly(_channel).ConfigureAwait(false);
            await DisposeQuietly(_connection).ConfigureAwait(false);
            _channel = null;
            _connection = null;
        }
        finally
        {
            _gate.Release();
        }
    }

    private static async Task DisposeQuietly(IAsyncDisposable? disposable)
    {
        if (disposable is null)
            return;
        try
        {
            await disposable.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Already broken; nothing to release.
        }
    }

    [LoggerMessage(EventId = 32, Level = LogLevel.Warning,
        Message = "The broker closed the channel during a batch; publishing the {Count} unconfirmed message(s) again one by one to isolate the cause.")]
    private static partial void LogIsolating(ILogger logger, int count);

    [LoggerMessage(EventId = 30, Level = LogLevel.Warning, Message = "Could not open a RabbitMQ channel; the batch is handed back.")]
    private static partial void LogConnectFailed(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 31, Level = LogLevel.Error,
        Message = "RabbitMQ exchange '{Exchange}' does not exist. Messages wait in the outbox until it is declared; Waybill does not create topology.")]
    private static partial void LogExchangeMissing(ILogger logger, string exchange);
}
