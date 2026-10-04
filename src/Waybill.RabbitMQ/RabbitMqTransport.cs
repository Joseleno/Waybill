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

    private readonly SemaphoreSlim _gate = new(1, 1);
    private IConnection? _connection;
    private IChannel? _channel;
    private bool _disposed;

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
            return batch.Select(_ => new PublishResult(PublishStatus.Retry, reason)).ToList();
        }

        // Publishes of one batch run concurrently on the channel; each completes when the broker confirms (or
        // returns, or nacks) that message.
        var exchange = options.Value.Exchange!;
        var publishes = batch.Select(message => PublishOneAsync(channel, exchange, message, cancellationToken));
        return await Task.WhenAll(publishes).ConfigureAwait(false);
    }

    private async Task<PublishResult> PublishOneAsync(IChannel channel, string exchange, OutgoingMessage message, CancellationToken cancellationToken)
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
            return PublishResult.Defect($"cannot be expressed as an AMQP message: {exception.Message}");
        }

        try
        {
            await channel.BasicPublishAsync(exchange, message.Name, mandatory: true, properties, message.Payload, cancellationToken)
                .ConfigureAwait(false);
            return PublishResult.Confirmed;
        }
        catch (PublishReturnException returned)
        {
            return PublishResult.Returned($"{returned.ReplyCode} {returned.ReplyText}");
        }
        catch (PublishException)
        {
            return new PublishResult(PublishStatus.Retry, "nacked by the broker");
        }
        catch (Exception exception)
        {
            // Closed channel or connection, cancellation at the publish timeout, or anything unexpected: the outcome
            // is unknown, so publish again. Never a defect of the message.
            return new PublishResult(PublishStatus.Retry, exception.Message);
        }
    }

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

    [LoggerMessage(EventId = 30, Level = LogLevel.Warning, Message = "Could not open a RabbitMQ channel; the batch is handed back.")]
    private static partial void LogConnectFailed(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 31, Level = LogLevel.Error,
        Message = "RabbitMQ exchange '{Exchange}' does not exist. Messages wait in the outbox until it is declared; Waybill does not create topology.")]
    private static partial void LogExchangeMissing(ILogger logger, string exchange);
}
