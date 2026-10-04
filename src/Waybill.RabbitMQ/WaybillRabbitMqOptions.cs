using RabbitMQ.Client;

namespace Waybill.RabbitMQ;

/// <summary>Configuration for the RabbitMQ transport. Set it up with <c>services.AddWaybillRabbitMQ(options => ...)</c>.</summary>
/// <remarks>
/// Waybill does not create topology: the exchange, queues and bindings belong to the application or its
/// infrastructure as code. Every message is published to <see cref="Exchange"/> with its registered name as routing
/// key (for example <c>billing.invoice-paid.v1</c>), so consumers bind by name or by pattern (<c>billing.#</c>).
/// </remarks>
public sealed class WaybillRabbitMqOptions
{
    /// <summary>Broker address, for example <c>amqp://user:password@rabbitmq:5672/vhost</c>. Required.</summary>
    public Uri? Uri { get; set; }

    /// <summary>Exchange every message is published to (a topic exchange, created by the application). Required.</summary>
    public string? Exchange { get; set; }

    /// <summary>Connection name shown in the RabbitMQ management UI. Default <c>waybill-dispatcher</c>.</summary>
    public string ClientProvidedName { get; set; } = "waybill-dispatcher";

    /// <summary>Optional further configuration of the connection factory (TLS, heartbeats, timeouts).</summary>
    public Action<ConnectionFactory>? ConfigureConnectionFactory { get; set; }
}
