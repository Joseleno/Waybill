using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Waybill.EntityFrameworkCore.Dispatching;

namespace Waybill.Tests.Unit;

// A zero sampling interval would spin the metrics loop against the database; it fails at startup instead.
public sealed class OpcoesDoDispatcherTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Dispatcher_MetricsIntervalNaoPositivo_FalhaNaPartida(int seconds)
    {
        using var services = new ServiceCollection()
            .AddLogging()
            .AddWaybillDispatcher(o =>
            {
                o.ConnectionString = "Host=localhost";
                o.MetricsInterval = TimeSpan.FromSeconds(seconds);
            })
            .BuildServiceProvider();

        var error = Assert.Throws<OptionsValidationException>(() => services.GetRequiredService<IOptions<WaybillDispatcherOptions>>().Value);
        Assert.Contains("WaybillDispatcherOptions.MetricsInterval", error.Message);
    }
}
