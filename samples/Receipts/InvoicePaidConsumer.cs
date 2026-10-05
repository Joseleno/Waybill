using System.Text.Json;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using RabbitMQ.Client.Exceptions;
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
        InvoicePaid paid;
        try
        {
            messageId = delivery.BasicProperties.GetWaybillMessageId();
            paid = JsonSerializer.Deserialize(delivery.Body.Span, ReceiptsJson.Default.InvoicePaid)
                ?? throw new JsonException("The message body is null.");
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException)
        {
            // Unreadable: retrying will not help. Reject without requeue (to a dead-letter exchange, if one is set).
            LogUnreadable(logger, exception);
            await channel.BasicRejectAsync(delivery.DeliveryTag, requeue: false, stoppingToken);
            return;
        }

        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var handler = scope.ServiceProvider.GetRequiredService<InvoicePaidHandler>();
            var result = await handler.HandleAsync(messageId, paid, stoppingToken);

            // Ack only after the inbox committed, whether this delivery was processed or a duplicate.
            await channel.BasicAckAsync(delivery.DeliveryTag, multiple: false, stoppingToken);
            LogHandled(logger, messageId, result);
        }
        catch (Exception exception) when (!stoppingToken.IsCancellationRequested)
        {
            // Nothing was committed (the inbox rolled back): the broker delivers it again. The queue's x-delivery-limit
            // (rabbitmq/definitions.json) bounds the retries and then dead-letters the message.
            LogFailed(logger, messageId, exception);
            await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
            await channel.BasicNackAsync(delivery.DeliveryTag, multiple: false, requeue: true, stoppingToken);
        }
    }

    private async Task<IConnection> ConnectAsync(ConnectionFactory factory, CancellationToken stoppingToken)
    {
        while (true)
        {
            try
            {
                return await factory.CreateConnectionAsync("receipts", stoppingToken);
            }
            catch (BrokerUnreachableException exception)
            {
                LogBrokerUnreachable(logger, exception);
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Consuming {Queue}.")]
    private static partial void LogConsuming(ILogger logger, string queue);

    [LoggerMessage(Level = LogLevel.Information, Message = "Message {MessageId}: {Result}.")]
    private static partial void LogHandled(ILogger logger, Guid messageId, Waybill.EntityFrameworkCore.InboxResult result);

    [LoggerMessage(Level = LogLevel.Error, Message = "Message {MessageId} failed; it will be delivered again.")]
    private static partial void LogFailed(ILogger logger, Guid messageId, Exception exception);

    [LoggerMessage(Level = LogLevel.Error, Message = "Unreadable message rejected.")]
    private static partial void LogUnreadable(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "RabbitMQ is unreachable; retrying in 5 s.")]
    private static partial void LogBrokerUnreachable(ILogger logger, Exception exception);
}

/// <summary>The broker address, shared by the consumer and the Waybill transport.</summary>
internal sealed record BrokerSettings(Uri Uri);
