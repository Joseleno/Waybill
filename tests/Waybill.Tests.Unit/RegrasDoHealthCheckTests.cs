using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Waybill.EntityFrameworkCore.Dispatching;

namespace Waybill.Tests.Unit;

// ADR 0004, health check rules in order: loop not running or stalled, then repeated database failures, are Unhealthy;
// the broker down (breaker open or a connection failure) is only Degraded, because the outbox keeps accepting events.
public sealed class RegrasDoHealthCheckTests
{
    private static readonly WaybillDispatcherOptions Options = new() { ConnectionString = "Host=localhost" };

    // Lease (20 s + 10 s) + MaxBackoff (30 s): a loop that has not finished a cycle for this long is stuck.
    private static readonly TimeSpan StallAfter = TimeSpan.FromSeconds(60);

    private readonly FakeTimeProvider _time = new();

    [Fact]
    public async Task HealthCheck_Regra_CicloComProgresso_Healthy()
    {
        var status = Started();
        status.CycleCompleted(DispatchOutcome.Progress);

        Assert.Equal(HealthStatus.Healthy, (await CheckAsync(status)).Status);
    }

    [Fact]
    public async Task HealthCheck_Regra_AindaSemCiclo_Healthy()
    {
        Assert.Equal(HealthStatus.Healthy, (await CheckAsync(Started())).Status);
    }

    [Theory]
    [InlineData(nameof(DispatchOutcome.ConnectionFailure))]
    [InlineData(nameof(DispatchOutcome.BreakerOpen))]
    public async Task HealthCheck_Regra_BrokerFora_Degraded(string outcome)
    {
        var status = Started();
        status.CycleCompleted(Enum.Parse<DispatchOutcome>(outcome));

        var result = await CheckAsync(status);

        Assert.Equal(HealthStatus.Degraded, result.Status);
        Assert.Contains("broker", result.Description);
    }

    [Fact]
    public async Task HealthCheck_Regra_Pressao_NaoEhDegraded()
    {
        var status = Started();
        status.CycleCompleted(DispatchOutcome.Pressure); // a slow broker: the batch shrinks, the breaker stays closed

        Assert.Equal(HealthStatus.Healthy, (await CheckAsync(status)).Status);
    }

    [Fact]
    public async Task HealthCheck_Regra_TresFalhasDeBancoSeguidas_Unhealthy_DuasNao()
    {
        var status = Started();
        status.CycleFailed();
        status.CycleFailed();
        Assert.NotEqual(HealthStatus.Unhealthy, (await CheckAsync(status)).Status);

        status.CycleFailed();
        var result = await CheckAsync(status);

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
        Assert.Contains("database", result.Description);
    }

    [Fact]
    public async Task HealthCheck_Regra_CicloBemSucedidoZeraAsFalhasDeBanco()
    {
        var status = Started();
        status.CycleFailed();
        status.CycleFailed();
        status.CycleCompleted(DispatchOutcome.Idle);
        status.CycleFailed();

        Assert.Equal(HealthStatus.Healthy, (await CheckAsync(status)).Status);
    }

    [Fact]
    public async Task HealthCheck_Regra_LacoParado_Unhealthy()
    {
        var status = Started();
        status.CycleCompleted(DispatchOutcome.Progress);
        status.Stopped();

        var result = await CheckAsync(status);

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
        Assert.Contains("not running", result.Description);
    }

    [Fact]
    public async Task HealthCheck_Regra_NuncaIniciado_Unhealthy()
    {
        Assert.Equal(HealthStatus.Unhealthy, (await CheckAsync(new DispatcherStatus(_time))).Status);
    }

    [Fact]
    public async Task HealthCheck_Regra_CicloTravadoAlemDoLeaseMaisBackoff_Unhealthy()
    {
        var status = Started();
        status.CycleCompleted(DispatchOutcome.ConnectionFailure);

        _time.Advance(StallAfter - TimeSpan.FromSeconds(1));
        Assert.Equal(HealthStatus.Degraded, (await CheckAsync(status)).Status);

        _time.Advance(TimeSpan.FromSeconds(2));
        var result = await CheckAsync(status);

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
        Assert.Contains("stalled", result.Description);
    }

    [Fact]
    public async Task HealthCheck_Regra_TravadoAntesDoPrimeiroCiclo_Unhealthy()
    {
        var status = Started();

        _time.Advance(StallAfter + TimeSpan.FromSeconds(1));

        Assert.Equal(HealthStatus.Unhealthy, (await CheckAsync(status)).Status);
    }

    private DispatcherStatus Started()
    {
        var status = new DispatcherStatus(_time);
        status.Started();
        return status;
    }

    private Task<HealthCheckResult> CheckAsync(DispatcherStatus status) =>
        new WaybillDispatcherHealthCheck(status, Microsoft.Extensions.Options.Options.Create(Options), metrics: null, _time)
            .CheckHealthAsync(new HealthCheckContext(), TestContext.Current.CancellationToken);
}
