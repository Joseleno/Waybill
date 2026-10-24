using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using Waybill;
using Waybill.EntityFrameworkCore;
using Waybill.RabbitMQ;

// Child process for the coexistence tests: Waybill 0.1.0-alpha, restored from nuget.org, against a database migrated by
// the code in src/. Only the protocol goes to stdout, one line per event; the first line is always the version of the
// Waybill assembly loaded. A long-running mode stops when the test closes stdin.
// Usage:
//   produce <connectionString> <count> <key>     key: "-" for none, "mod:N" for legacy-0 .. legacy-(N-1), else that key
//   dispatch <connectionString>                  the hosted 0.1 dispatcher, with a transport that prints and confirms
//   consume <connectionString> <amqpUri> <queue> <handler>
//   claim-sql                                    the 0.1 claim statement, base64 (it is a private constant of the package)
var version = typeof(WaybillOptions).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion;
Emit($"VERSION {version}");

switch (args[0])
{
    case "produce":
        await ProduceAsync(args[1], int.Parse(args[2], CultureInfo.InvariantCulture), args[3]);
        break;
    case "dispatch":
        await DispatchAsync(args[1]);
        break;
    case "consume":
        await ConsumeAsync(args[1], new Uri(args[2]), args[3], args[4]);
        break;
    case "claim-sql":
        var store = typeof(WaybillSchema).Assembly.GetType("Waybill.EntityFrameworkCore.Dispatching.OutboxStore", throwOnError: true)!;
        var sql = (string)store.GetField("ClaimSql", BindingFlags.NonPublic | BindingFlags.Static)!.GetRawConstantValue()!;
        Emit($"CLAIM-SQL {Convert.ToBase64String(Encoding.UTF8.GetBytes(sql))}");
        break;
    default:
        throw new ArgumentException($"unknown mode {args[0]}");
}
Emit("DONE");

static void Emit(string line)
{
    Console.WriteLine(line);
    Console.Out.Flush();
}

static HostApplicationBuilder Builder(string connectionString)
{
    var builder = Host.CreateApplicationBuilder();
    builder.Logging.ClearProviders();
    builder.Services.AddDbContext<LegacyContext>(o => o.UseNpgsql(connectionString));
    builder.Services.AddWaybill(o =>
    {
        o.MaxPayloadBytes = 64 * 1024;
        o.AddMessage("billing.invoice-paid.v1", LegacyJson.Default.InvoicePaid);
    });
    return builder;
}

// Each message in a SaveChanges of its own, as a request would write it.
static async Task ProduceAsync(string connectionString, int count, string key)
{
    var builder = Builder(connectionString);
    builder.Services.AddWaybillOutbox<LegacyContext>();
    using var host = builder.Build();
    for (var i = 0; i < count; i++)
    {
        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LegacyContext>();
        var outbox = scope.ServiceProvider.GetRequiredService<IOutbox<LegacyContext>>();
        var id = outbox.Enqueue(new InvoicePaid(Guid.NewGuid(), i), key switch
        {
            "-" => null,
            _ when key.StartsWith("mod:", StringComparison.Ordinal) => $"legacy-{i % int.Parse(key[4..], CultureInfo.InvariantCulture)}",
            _ => key,
        });
        await db.SaveChangesAsync();
        Emit($"ENQUEUED {id}");
    }
}

static async Task DispatchAsync(string connectionString)
{
    var builder = Builder(connectionString);
    builder.Services.AddWaybillDispatcher(o =>
    {
        o.ConnectionString = connectionString;
        o.PollingInterval = TimeSpan.FromMilliseconds(100);
    });
    builder.Services.AddSingleton<ITransport, PrintingTransport>();
    using var host = builder.Build();
    await host.StartAsync();
    Emit("STARTED");
    await StdinClosedAsync();
    await host.StopAsync();
}

// The application's own consumer, as the 0.1 README shows: the inbox in the handler's transaction, ack after the commit.
static async Task ConsumeAsync(string connectionString, Uri amqp, string queue, string handler)
{
    var builder = Builder(connectionString);
    builder.Services.AddWaybillInbox<LegacyContext>();
    using var host = builder.Build();
    await using var connection = await new ConnectionFactory { Uri = amqp }.CreateConnectionAsync();
    await using var channel = await connection.CreateChannelAsync();
    await channel.BasicQosAsync(0, 1, false);
    var consumer = new AsyncEventingBasicConsumer(channel);
    consumer.ReceivedAsync += async (_, delivery) =>
    {
        var messageId = delivery.BasicProperties.GetWaybillMessageId();
        using var scope = host.Services.CreateScope();
        var inbox = scope.ServiceProvider.GetRequiredService<IInbox<LegacyContext>>();
        var result = await inbox.ProcessAsync(handler, messageId, (_, _) => Task.CompletedTask);
        await channel.BasicAckAsync(delivery.DeliveryTag, false);
        Emit($"{(result == InboxResult.Processed ? "PROCESSED" : "DUPLICATE")} {messageId}");
    };
    await channel.BasicConsumeAsync(queue, autoAck: false, consumer);
    Emit("STARTED");
    await StdinClosedAsync();
}

static async Task StdinClosedAsync()
{
    while (await Console.In.ReadLineAsync() is not null)
    {
    }
}

internal sealed record InvoicePaid(Guid InvoiceId, decimal Amount);

[JsonSerializable(typeof(InvoicePaid))]
internal sealed partial class LegacyJson : JsonSerializerContext;

internal sealed class LegacyContext(DbContextOptions<LegacyContext> options) : DbContext(options)
{
    protected override void OnModelCreating(ModelBuilder modelBuilder) => modelBuilder.MapWaybillOutbox();
}

// Confirms every message and prints its id. WAYBILL_LEGACY_DROP_EVERY=N confirms every Nth without printing it: the
// mutation that shows the tests' oracle sees a lost message.
internal sealed class PrintingTransport : ITransport
{
    private static readonly int DropEvery =
        int.TryParse(Environment.GetEnvironmentVariable("WAYBILL_LEGACY_DROP_EVERY"), CultureInfo.InvariantCulture, out var n) ? n : 0;

    private int _seen;

    public Task<IReadOnlyList<PublishResult>> PublishAsync(IReadOnlyList<OutgoingMessage> batch, CancellationToken cancellationToken)
    {
        foreach (var message in batch)
        {
            if (DropEvery == 0 || ++_seen % DropEvery != 0)
                Console.WriteLine($"PUBLISHED {message.MessageId}");
        }
        Console.Out.Flush();
        return Task.FromResult<IReadOnlyList<PublishResult>>(batch.Select(_ => PublishResult.Confirmed).ToList());
    }
}
