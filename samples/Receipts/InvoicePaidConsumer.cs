using System.Text.Json;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using Waybill;
using Waybill.EntityFrameworkCore;
using Waybill.RabbitMQ;

namespace Receipts;

/// <summary>
/// A plain RabbitMQ.Client consumer: no consumption framework. Waybill only wraps the handler (the inbox); reading,
/// acking and retrying stay here, in the application's hands.
/// </summary>
internal sealed partial class InvoicePaidConsumer(
    IServiceScopeFactory scopes, BrokerSettings broker, ILogger<InvoicePaidConsumer> logger) : BackgroundService
{
    public const string Queue = "receipts.invoice-paid";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Automatic recovery is on by default: after a broker outage the client reconnects and resumes this consumer.
        await using var connection = await ConnectAsync(new ConnectionFactory { Uri = broker.Uri }, stoppingToken);
        await using var channel = await connection.CreateChannelAsync(cancellationToken: stoppingToken);
        await channel.BasicQosAsync(prefetchSize: 0, prefetchCount: 1, global: false, stoppingToken);

        var consumer = new AsyncEventingBasicConsumer(channel);
        consumer.ReceivedAsync += (_, delivery) => HandleAsync(channel, delivery, stoppingToken);
        await channel.BasicConsumeAsync(Queue, autoAck: false, consumer, stoppingToken);
        LogConsuming(logger, Queue);

        try
        {
            await Task.Delay(Timeout.Infinite, stoppingToken);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task HandleAsync(IChannel channel, BasicDeliverEventArgs delivery, CancellationToken stoppingToken)
    {
        Guid messageId;
        MessageOrder? order;
        InvoicePaid paid;
        try
        {
            messageId = delivery.BasicProperties.GetWaybillMessageId();
            order = delivery.BasicProperties.GetWaybillOrder(); // null unless Billing orders by key
            paid = JsonSerializer.Deserialize(delivery.Body.Span, ReceiptsJson.Default.InvoicePaid)
                ?? throw new JsonException("The message body is null.");
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException)
        {
            // Unreadable: retrying will not help. Reject without requeue: it goes to the dead-letter queue.
            LogUnreadable(logger, exception);
            await channel.BasicRejectAsync(delivery.DeliveryTag, requeue: false, stoppingToken);
            return;
        }

        // A failure is retried here, waiting longer each time (1, 2, 4 … up to 30 s; about two and a half minutes in
        // all), so a database restart is ridden out. Not requeued: a nack with requeue does not count toward the queue's
        // x-delivery-limit, so a message that always fails would come back for ever and, with one active consumer, hold
        // the whole queue. Past the last attempt it goes to the dead-letter queue: the invoice's next message is then
        // applied, a gap, and a replay of this one comes back Regressed. Someone has to look at receipts.invoice-paid.dead.
        OrderedInboxResult result;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                result = await scope.ServiceProvider.GetRequiredService<InvoicePaidHandler>().HandleAsync(messageId, order, paid, stoppingToken);
                break;
            }
            catch (Exception exception) when (!stoppingToken.IsCancellationRequested && attempt < MaxAttempts)
            {
                // Nothing was committed: the inbox rolled back.
                var delay = TimeSpan.FromSeconds(Math.Min(Math.Pow(2, attempt - 1), 30));
                LogFailed(logger, messageId, attempt, delay, exception);
                await Task.Delay(delay, stoppingToken);
            }
            catch (Exception exception) when (!stoppingToken.IsCancellationRequested)
            {
                LogGaveUp(logger, messageId, attempt, exception);
                await channel.BasicNackAsync(delivery.DeliveryTag, multiple: false, requeue: false, stoppingToken);
                return;
            }
        }

        try
        {
            if (result == OrderedInboxResult.Regressed)
            {
                // Older than a message already applied for its invoice: nothing was applied, and the inbox logged why
                // (EventId 50). To the dead-letter queue, never acknowledged as processed.
                await channel.BasicNackAsync(delivery.DeliveryTag, multiple: false, requeue: false, stoppingToken);
            }
            else
            {
                // Ack only after the inbox committed, whether this delivery was processed or a duplicate.
                await channel.BasicAckAsync(delivery.DeliveryTag, multiple: false, stoppingToken);
            }
            LogHandled(logger, messageId, result);
        }
        catch (Exception exception) when (!stoppingToken.IsCancellationRequested)
        {
            // The channel closed: the broker delivers it again. Processed, it comes back Duplicate; regressed, Regressed.
            LogAckFailed(logger, messageId, exception);
        }
    }

    private const int MaxAttempts = 10;

    private async Task<IConnection> ConnectAsync(ConnectionFactory factory, CancellationToken stoppingToken)
    {
        while (true)
        {
            try
            {
                return await factory.CreateConnectionAsync("receipts", stoppingToken);
            }
            catch (Exception exception) when (!stoppingToken.IsCancellationRequested)
            {
                LogBrokerUnreachable(logger, exception);
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Consuming {Queue}.")]
    private static partial void LogConsuming(ILogger logger, string queue);

    [LoggerMessage(Level = LogLevel.Information, Message = "Message {MessageId}: {Result}.")]
    private static partial void LogHandled(ILogger logger, Guid messageId, OrderedInboxResult result);

    [LoggerMessage(Level = LogLevel.Error, Message = "Message {MessageId} failed (attempt {Attempt}); trying again in {Delay}.")]
    private static partial void LogFailed(ILogger logger, Guid messageId, int attempt, TimeSpan delay, Exception exception);

    [LoggerMessage(Level = LogLevel.Error, Message = "Message {MessageId} failed {Attempt} times; sent to the dead-letter queue. Its invoice's next message will be applied without it.")]
    private static partial void LogGaveUp(ILogger logger, Guid messageId, int attempt, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Message {MessageId} was handled but the ack or nack failed; it will be delivered again.")]
    private static partial void LogAckFailed(ILogger logger, Guid messageId, Exception exception);

    [LoggerMessage(Level = LogLevel.Error, Message = "Unreadable message rejected.")]
    private static partial void LogUnreadable(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Cannot connect to RabbitMQ; retrying in 5 s.")]
    private static partial void LogBrokerUnreachable(ILogger logger, Exception exception);
}

/// <summary>The broker address, shared by the consumer and the Waybill transport.</summary>
internal sealed record BrokerSettings(Uri Uri);
