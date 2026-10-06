using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Waybill;
using Waybill.EntityFrameworkCore.Dispatching;

// Child process for the chaos tests. Runs the real hosted dispatcher through the public API only, with a transport
// that announces the batch it got and then never returns (a stuck publish). The test kills this process with
// Process.Kill — the equivalent of kill -9: no finally blocks, no graceful shutdown, no hand-back.
// Usage: Waybill.Tests.ChaosHost <connectionString> <batchSize> <publishTimeoutMs> <leaseMarginMs>
var builder = Host.CreateApplicationBuilder();
builder.Logging.ClearProviders();
builder.Services.AddWaybill(o => o.MaxPayloadBytes = 64 * 1024);
builder.Services.AddWaybillDispatcher(o =>
{
    o.ConnectionString = args[0];
    o.BatchSize = int.Parse(args[1], CultureInfo.InvariantCulture);
    o.PublishTimeout = TimeSpan.FromMilliseconds(int.Parse(args[2], CultureInfo.InvariantCulture));
    o.LeaseMargin = TimeSpan.FromMilliseconds(int.Parse(args[3], CultureInfo.InvariantCulture));
});
builder.Services.AddSingleton<ITransport, StuckTransport>();
await builder.Build().RunAsync();

internal sealed class StuckTransport : ITransport
{
    public async Task<IReadOnlyList<PublishResult>> PublishAsync(IReadOnlyList<OutgoingMessage> batch, CancellationToken cancellationToken)
    {
        Console.WriteLine($"PUBLISHING {batch.Count}");
        Console.Out.Flush();
        await Task.Delay(Timeout.Infinite, CancellationToken.None); // ignores the timeout: stuck, like a hung socket
        throw new InvalidOperationException("unreachable");
    }
}
