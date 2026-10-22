using System.Globalization;
using System.Text.Json;
using DotNet.Testcontainers.Containers;
using RabbitMQ.Client;

namespace Waybill.Tests.Integration.G2.RabbitMq;

/// <summary>The sample's broker topology and queue counts, shared by the integration and chaos tests.</summary>
public static class SampleTopology
{
    /// <summary>
    /// The sample's topology, read from <c>samples/rabbitmq/definitions.json</c> so the tests and the sample cannot drift
    /// apart: every exchange, queue and binding with its arguments, each name suffixed to stay unique to the test.
    /// </summary>
    /// <returns>A map from the sample's names to the declared ones.</returns>
    public static async Task<IReadOnlyDictionary<string, string>> DeclareAsync(IConnection connection)
    {
        var suffix = "-" + Guid.NewGuid().ToString("N")[..8];
        using var definitions = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(FindRoot(), "samples", "rabbitmq", "definitions.json")));
        var root = definitions.RootElement;
        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        string Named(string name) => names.TryGetValue(name, out var declared) ? declared : names[name] = name + suffix;

        await using var channel = await connection.CreateChannelAsync();
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

    /// <summary>
    /// A queue's counts as the broker keeps them: every message, ready ones, unacknowledged ones. A dead letter the
    /// target refused (at-least-once dead-lettering) is in the first and in neither of the others.
    /// </summary>
    public static async Task<(long Messages, long Ready, long Unacked)> QueueCountsAsync(IContainer rabbit, string queue)
    {
        var result = await rabbit.ExecAsync(["rabbitmqctl", "list_queues", "--quiet", "--no-table-headers", "name", "messages", "messages_ready", "messages_unacknowledged"]);
        var row = result.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Split('\t', StringSplitOptions.TrimEntries))
            .Single(columns => columns[0] == queue);
        return (long.Parse(row[1], CultureInfo.InvariantCulture), long.Parse(row[2], CultureInfo.InvariantCulture), long.Parse(row[3], CultureInfo.InvariantCulture));
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
}
