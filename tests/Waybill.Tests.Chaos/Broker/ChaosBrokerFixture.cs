using System.Net.Http.Json;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using DotNet.Testcontainers.Networks;
using RabbitMQ.Client;
using Testcontainers.RabbitMq;

namespace Waybill.Tests.Chaos.Broker;

/// <summary>
/// RabbitMQ behind Toxiproxy on a private Docker network. Waybill connects through the proxy (<see cref="ProxiedUri"/>),
/// so the test can cut the path, add latency or drop connections; the test itself declares topology and reads queues
/// directly (<see cref="DirectUri"/>). Toxiproxy is driven through its HTTP API.
/// </summary>
public sealed class ChaosBrokerFixture : IAsyncLifetime
{
    private const int ApiPort = 8474;
    private const int ProxyPort = 8666;
    private const string Proxy = "rabbitmq";

    private readonly INetwork _network = new NetworkBuilder().Build();
    private readonly RabbitMqContainer _rabbit;
    private readonly IContainer _toxiproxy;
    private HttpClient _api = null!;
    private IConnection _direct = null!;

    public ChaosBrokerFixture()
    {
        _rabbit = new RabbitMqBuilder("rabbitmq:4-alpine").WithNetwork(_network).WithNetworkAliases("rabbitmq").Build();
        _toxiproxy = new ContainerBuilder("ghcr.io/shopify/toxiproxy:2.12.0")
            .WithNetwork(_network)
            .WithPortBinding(ApiPort, true)
            .WithPortBinding(ProxyPort, true)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(r => r.ForPort(ApiPort).ForPath("/version")))
            .Build();
    }

    public Uri DirectUri => new(_rabbit.GetConnectionString());

    public Uri ProxiedUri => new UriBuilder(DirectUri) { Host = _toxiproxy.Hostname, Port = _toxiproxy.GetMappedPublicPort(ProxyPort) }.Uri;

    public async ValueTask InitializeAsync()
    {
        await _network.CreateAsync();
        await Task.WhenAll(_rabbit.StartAsync(), _toxiproxy.StartAsync());
        _api = new HttpClient { BaseAddress = new Uri($"http://{_toxiproxy.Hostname}:{_toxiproxy.GetMappedPublicPort(ApiPort)}") };
        (await _api.PostAsJsonAsync("/proxies", new { name = Proxy, listen = $"0.0.0.0:{ProxyPort}", upstream = "rabbitmq:5672", enabled = true }))
            .EnsureSuccessStatusCode();
        _direct = await new ConnectionFactory { Uri = DirectUri }.CreateConnectionAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await _direct.DisposeAsync();
        _api.Dispose();
        await _toxiproxy.DisposeAsync();
        await _rabbit.DisposeAsync();
        await _network.DisposeAsync();
    }

    /// <summary>Cuts (false) or restores (true) the path to the broker. Cutting also drops every open connection.</summary>
    public async Task SetBrokerReachableAsync(bool reachable) =>
        (await _api.PostAsJsonAsync($"/proxies/{Proxy}", new { enabled = reachable })).EnsureSuccessStatusCode();

    public async Task AddLatencyAsync(int milliseconds) =>
        (await _api.PostAsJsonAsync($"/proxies/{Proxy}/toxics",
            new { name = "latency", type = "latency", stream = "downstream", toxicity = 1.0, attributes = new { latency = milliseconds } }))
        .EnsureSuccessStatusCode();

    public async Task RemoveLatencyAsync() => (await _api.DeleteAsync($"/proxies/{Proxy}/toxics/latency")).EnsureSuccessStatusCode();

    /// <summary>The application's topology: a topic exchange and a quorum queue bound with <c>billing.#</c>, unique to the test.</summary>
    public async Task<(string Exchange, string Queue)> DeclareTopologyAsync()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        await using var channel = await _direct.CreateChannelAsync();
        await channel.ExchangeDeclareAsync($"events-{suffix}", ExchangeType.Topic, durable: true);
        await channel.QueueDeclareAsync($"billing-{suffix}", durable: true, exclusive: false, autoDelete: false,
            arguments: new Dictionary<string, object?> { ["x-queue-type"] = "quorum" });
        await channel.QueueBindAsync($"billing-{suffix}", $"events-{suffix}", "billing.#");
        return ($"events-{suffix}", $"billing-{suffix}");
    }

    /// <summary>Message ids delivered to <paramref name="queue"/> (drains it).</summary>
    public async Task<List<string>> DrainMessageIdsAsync(string queue)
    {
        await using var channel = await _direct.CreateChannelAsync();
        var ids = new List<string>();
        while (await channel.BasicGetAsync(queue, autoAck: true) is { } message)
            ids.Add(message.BasicProperties.MessageId!);
        return ids;
    }
}

[CollectionDefinition(Name)]
public sealed class ChaosBrokerCollection : ICollectionFixture<Integration.PostgresFixture>, ICollectionFixture<ChaosBrokerFixture>
{
    public const string Name = "chaos-broker";
}
