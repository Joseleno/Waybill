using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Waybill.EntityFrameworkCore.Dispatching;

namespace Waybill.Tests.Unit;

// Intervals the dispatcher waits on fail at startup when out of range: zero would spin against the database, and past
// ~49 days Task.Delay throws and stops the host; nothing needs more than a day.
public sealed class OpcoesDoDispatcherTests
{
    public static TheoryData<string, Action<WaybillDispatcherOptions>> Invalidas => new()
    {
        { "MetricsInterval", o => o.MetricsInterval = TimeSpan.Zero },
        { "MetricsInterval", o => o.MetricsInterval = TimeSpan.FromSeconds(-1) },
        { "MetricsInterval", o => o.MetricsInterval = TimeSpan.FromDays(2) },
        { "PollingInterval", o => o.PollingInterval = TimeSpan.FromDays(2) },
        { "ReturnBackoff", o => o.ReturnBackoff = TimeSpan.FromSeconds(-1) },
        { "MaxReturnBackoff", o => o.MaxReturnBackoff = TimeSpan.FromSeconds(30) }, // below the 1 min ReturnBackoff
        { "MaxReturnBackoff", o => o.MaxReturnBackoff = TimeSpan.FromDays(2) },
    };

    [Theory]
    [MemberData(nameof(Invalidas))]
    public void Dispatcher_IntervaloForaDoLimite_FalhaNaPartidaDizendoQual(string option, Action<WaybillDispatcherOptions> breakIt)
    {
        using var services = new ServiceCollection()
            .AddLogging()
            .AddWaybillDispatcher(o =>
            {
                o.ConnectionString = "Host=localhost";
                breakIt(o);
            })
            .BuildServiceProvider();

        var error = Assert.Throws<OptionsValidationException>(() => services.GetRequiredService<IOptions<WaybillDispatcherOptions>>().Value);
        Assert.Contains($"WaybillDispatcherOptions.{option}", error.Message);
    }

    public static TheoryData<string, Action<WaybillDispatcherOptions>> ValidasNaFronteira => new()
    {
        { "no wait after a return, as in 0.1", o => { o.ReturnBackoff = TimeSpan.Zero; o.MaxReturnBackoff = TimeSpan.Zero; } },
        { "a one-day ceiling", o => o.MaxReturnBackoff = TimeSpan.FromDays(1) },
        { "wait equal to its ceiling", o => { o.ReturnBackoff = TimeSpan.FromDays(1); o.MaxReturnBackoff = TimeSpan.FromDays(1); } },
    };

    [Theory]
    [MemberData(nameof(ValidasNaFronteira))]
    public void Dispatcher_EsperaEntreRetornosNaFronteira_SobeNormalmente(string why, Action<WaybillDispatcherOptions> configure)
    {
        using var services = new ServiceCollection()
            .AddLogging()
            .AddWaybillDispatcher(o =>
            {
                o.ConnectionString = "Host=localhost";
                configure(o);
            })
            .BuildServiceProvider();

        Assert.NotNull(services.GetRequiredService<IOptions<WaybillDispatcherOptions>>().Value);
        Assert.False(string.IsNullOrEmpty(why));
    }
}
