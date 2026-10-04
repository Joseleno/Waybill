using RabbitMQ.Client;
using Testcontainers.RabbitMq;

namespace Waybill.Tests.Integration.G2.RabbitMq;

/// <summary>A RabbitMQ broker for the collection. Each test declares its own exchange and queue, so tests stay isolated.</summary>
public sealed class RabbitMqFixture : IAsyncLifetime
{
    private readonly RabbitMqContainer _container = new RabbitMqBuilder("rabbitmq:4-alpine").Build();
    private IConnection? _connection;

    public Uri Uri => new(_container.GetConnectionString());

    public async ValueTask InitializeAsync()
    {
        await _container.StartAsync();
        _connection = await new ConnectionFactory { Uri = Uri }.CreateConnectionAsync();
    }

    public async ValueTask DisposeAsync()
    {
        if (_connection is not null)
            await _connection.DisposeAsync();
        await _container.DisposeAsync();
    }

    /// <summary>
    /// The application's topology, which Waybill never creates: a topic exchange and a quorum queue bound with
    /// <paramref name="bindingKey"/>. Returns the exchange and queue names, unique to the test.
    /// </summary>
    public async Task<(string Exchange, string Queue)> DeclareTopologyAsync(string bindingKey = "billing.#", bool declareExchange = true)
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var (exchange, queue) = ($"events-{suffix}", $"billing-{suffix}");
        await using var channel = await _connection!.CreateChannelAsync();
        await channel.QueueDeclareAsync(queue, durable: true, exclusive: false, autoDelete: false,
            arguments: new Dictionary<string, object?> { ["x-queue-type"] = "quorum" });
        if (declareExchange)
            await DeclareExchangeAsync(exchange, queue, bindingKey);
        return (exchange, queue);
    }

    public async Task DeclareExchangeAsync(string exchange, string queue, string bindingKey = "billing.#")
    {
        await using var channel = await _connection!.CreateChannelAsync();
        await channel.ExchangeDeclareAsync(exchange, ExchangeType.Topic, durable: true);
        await channel.QueueBindAsync(queue, exchange, bindingKey);
    }

    /// <summary>Drains up to <paramref name="max"/> messages from <paramref name="queue"/>.</summary>
    public async Task<List<BasicGetResult>> DrainAsync(string queue, int max = 1000)
    {
        await using var channel = await _connection!.CreateChannelAsync();
        var messages = new List<BasicGetResult>();
        while (messages.Count < max && await channel.BasicGetAsync(queue, autoAck: true) is { } message)
            messages.Add(message);
        return messages;
    }
}

[CollectionDefinition(Name)]
public sealed class BrokerCollection : ICollectionFixture<PostgresFixture>, ICollectionFixture<RabbitMqFixture>
{
    public const string Name = "postgres-rabbitmq";
}
