using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Waybill.EntityFrameworkCore.Retention;

namespace Waybill.Tests.Unit;

// A misconfigured retention fails at startup, not by deleting the wrong rows later: a zero or negative retention
// would make every published row (and every inbox row) eligible at once.
public sealed class OpcoesDeRetencaoTests
{
    public static TheoryData<string, Action<WaybillRetentionOptions>> Invalidas => new()
    {
        { "ConnectionString", o => o.ConnectionString = " " },
        { "OutboxRetention", o => o.OutboxRetention = TimeSpan.Zero },
        { "OutboxRetention", o => o.OutboxRetention = TimeSpan.FromDays(-1) },
        { "InboxRetention", o => o.InboxRetention = TimeSpan.Zero },
        { "InboxRetention", o => o.InboxRetention = TimeSpan.FromDays(-1) },
        { "Interval", o => o.Interval = TimeSpan.Zero },
        { "BatchSize", o => o.BatchSize = 0 },
    };

    [Theory]
    [MemberData(nameof(Invalidas))]
    public void Retencao_OpcaoInvalida_FalhaNaPartidaDizendoQual(string option, Action<WaybillRetentionOptions> breakIt)
    {
        using var services = Build(o =>
        {
            o.ConnectionString = "Host=localhost";
            breakIt(o);
        });

        var error = Assert.Throws<OptionsValidationException>(() => services.GetRequiredService<IOptions<WaybillRetentionOptions>>().Value);
        Assert.Contains($"WaybillRetentionOptions.{option}", error.Message);
    }

    [Fact]
    public void Retencao_SoAConnectionString_DefaultsValidos()
    {
        using var services = Build(o => o.ConnectionString = "Host=localhost");

        Assert.NotNull(services.GetRequiredService<IOptions<WaybillRetentionOptions>>().Value);
    }

    private static ServiceProvider Build(Action<WaybillRetentionOptions> configure) =>
        new ServiceCollection().AddLogging().AddWaybillRetention(configure).BuildServiceProvider();
}
