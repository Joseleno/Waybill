using System.Text.Json;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
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
        InvoicePaid paid;
        try
        {
            messageId = delivery.BasicProperties.GetWaybillMessageId();
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

        InboxResult result;
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            result = await scope.ServiceProvider.GetRequiredService<InvoicePaidHandler>().HandleAsync(messageId, paid, stoppingToken);
        }
        catch (Exception exception) when (!stoppingToken.IsCancellationRequested)
        {
            // Nothing was committed (the inbox rolled back). Wait longer at each redelivery (1, 2, 4 … up to 30 s), so a
            // database restart is ridden out; past the queue's x-delivery-limit (definitions.json) the broker
            // dead-letters the message, and someone has to look at receipts.invoice-paid.dead.
            var delay = TimeSpan.FromSeconds(Math.Min(Math.Pow(2, DeliveryCount(delivery)), 30));
            LogFailed(logger, messageId, delay, exception);
            await Task.Delay(delay, stoppingToken);
            await channel.BasicNackAsync(delivery.DeliveryTag, multiple: false, requeue: true, stoppingToken);
            return;
        }

        try
        {
            // Ack only after the inbox committed, whether this delivery was processed or a duplicate.
            await channel.BasicAckAsync(delivery.DeliveryTag, multiple: false, stoppingToken);
            LogHandled(logger, messageId, result);
        }
        catch (Exception exception) when (!stoppingToken.IsCancellationRequested)
        {
            // Committed but not acked (the channel closed): the broker redelivers it and the inbox returns Duplicate.
            LogAckFailed(logger, messageId, exception);
        }
    }

    // Quorum queues count redeliveries in x-delivery-count; absent on the first delivery.
    private static long DeliveryCount(BasicDeliverEventArgs delivery) =>
        delivery.BasicProperties.Headers?.TryGetValue("x-delivery-count", out var count) == true && count is long n ? n : 0;

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
    private static partial void LogHandled(ILogger logger, Guid messageId, InboxResult result);

    [LoggerMessage(Level = LogLevel.Error, Message = "Message {MessageId} failed; it will be delivered again in {Delay}.")]
    private static partial void LogFailed(ILogger logger, Guid messageId, TimeSpan delay, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Message {MessageId} committed but the ack failed; the redelivery will be a duplicate.")]
    private static partial void LogAckFailed(ILogger logger, Guid messageId, Exception exception);

    [LoggerMessage(Level = LogLevel.Error, Message = "Unreadable message rejected.")]
    private static partial void LogUnreadable(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Cannot connect to RabbitMQ; retrying in 5 s.")]
    private static partial void LogBrokerUnreachable(ILogger logger, Exception exception);
}

/// <summary>The broker address, shared by the consumer and the Waybill transport.</summary>
internal sealed record BrokerSettings(Uri Uri);
