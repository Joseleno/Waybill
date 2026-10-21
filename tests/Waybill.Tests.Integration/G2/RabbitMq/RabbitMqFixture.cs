using System.Text.Json;
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

    /// <summary>
    /// The sample's topology, read from <c>samples/rabbitmq/definitions.json</c> so the tests and the sample cannot drift
    /// apart: every exchange, queue and binding with its arguments, each name suffixed to stay unique to the test.
    /// </summary>
    /// <returns>A map from the sample's names to the declared ones.</returns>
    public async Task<IReadOnlyDictionary<string, string>> DeclareSampleTopologyAsync()
    {
        var suffix = "-" + Guid.NewGuid().ToString("N")[..8];
        using var definitions = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(FindRoot(), "samples", "rabbitmq", "definitions.json")));
        var root = definitions.RootElement;
        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        string Named(string name) => names.TryGetValue(name, out var declared) ? declared : names[name] = name + suffix;

        await using var channel = await _connection!.CreateChannelAsync();
        foreach (var exchange in root.GetProperty("exchanges").EnumerateArray())
        {
            await channel.ExchangeDeclareAsync(Named(exchange.GetProperty("name").GetString()!), exchange.GetProperty("type").GetString()!,
                durable: true, autoDelete: false, arguments: Arguments(exchange, Named));
        }
        foreach (var queue in root.GetProperty("queues").EnumerateArray())
        {
            await channel.QueueDeclareAsync(Named(queue.GetProperty("name").GetString()!), durable: true, exclusive: false, autoDelete: false,
                arguments: Arguments(queue, Named));
        }
        foreach (var binding in root.GetProperty("bindings").EnumerateArray())
        {
            await channel.QueueBindAsync(Named(binding.GetProperty("destination").GetString()!), Named(binding.GetProperty("source").GetString()!),
                binding.GetProperty("routing_key").GetString()!);
        }
        return names;
    }

    // Arguments as the definitions import reads them; names that point at another declared object (a dead-letter
    // exchange) get the same suffix.
    private static Dictionary<string, object?> Arguments(JsonElement declaration, Func<string, string> named) =>
        declaration.GetProperty("arguments").EnumerateObject().ToDictionary(a => a.Name, a => a.Value.ValueKind switch
        {
            JsonValueKind.Number => (object?)a.Value.GetInt64(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ when a.Name == "x-dead-letter-exchange" => named(a.Value.GetString()!),
            _ => a.Value.GetString(),
        });

    private static string FindRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Waybill.slnx")))
                return dir.FullName;
        }
        throw new InvalidOperationException("Waybill.slnx not found above " + AppContext.BaseDirectory);
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
