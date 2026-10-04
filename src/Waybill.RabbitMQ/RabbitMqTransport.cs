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
        try
        {
            await channel.BasicPublishAsync(exchange, message.Name, mandatory: true, Properties(message), message.Payload, cancellationToken)
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

    private async Task<IChannel> GetChannelAsync(CancellationToken cancellationToken)
    {
        if (_channel is { IsOpen: true } open)
            return open;

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_channel is { IsOpen: true } reopened)
                return reopened;

            var settings = options.Value;
            if (_connection is not { IsOpen: true })
            {
                await DisposeQuietly(_connection).ConfigureAwait(false);
                var factory = new ConnectionFactory
                {
                    Uri = settings.Uri!,
                    ClientProvidedName = settings.ClientProvidedName,
                    AutomaticRecoveryEnabled = false,
                };
                settings.ConfigureConnectionFactory?.Invoke(factory);
                _connection = await factory.CreateConnectionAsync(cancellationToken).ConfigureAwait(false);
            }

            await DisposeQuietly(_channel).ConfigureAwait(false);
            var channel = await _connection.CreateChannelAsync(
                new CreateChannelOptions(publisherConfirmationsEnabled: true, publisherConfirmationTrackingEnabled: true),
                cancellationToken).ConfigureAwait(false);

            // The exchange is the application's topology. A missing one would close the channel on every publish;
            // say so plainly instead.
            if (settings.Exchange!.Length > 0)
            {
                try
                {
                    await channel.ExchangeDeclarePassiveAsync(settings.Exchange, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationInterruptedException missing)
                {
                    await DisposeQuietly(channel).ConfigureAwait(false);
                    LogExchangeMissing(logger, settings.Exchange);
                    throw new InvalidOperationException(
                        $"Exchange '{settings.Exchange}' does not exist. Waybill does not create topology: declare it in the application or its infrastructure.",
                        missing);
                }
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
        await DisposeQuietly(_channel).ConfigureAwait(false);
        await DisposeQuietly(_connection).ConfigureAwait(false);
        _gate.Dispose();
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
