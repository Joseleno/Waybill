using Microsoft.Extensions.Time.Testing;
using Waybill.EntityFrameworkCore.Dispatching;

namespace Waybill.Tests.Unit;

// ADR 0003: the breaker opens only on connection or channel failures and doubles its open period; the batch halves
// on confirmation timeouts and nacks and grows back after healthy batches.
public sealed class BreakerELoteTests
{
    private static readonly TimeSpan Base = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan Max = TimeSpan.FromSeconds(30);

    [Fact]
    public void Breaker_FechadoPorPadrao()
    {
        var breaker = new CircuitBreaker(new FakeTimeProvider(), Base, Max);

        Assert.False(breaker.IsOpen);
        Assert.False(breaker.IsHalfOpen);
    }

    [Fact]
    public void Breaker_FalhaDeConexao_AbreDepoisMeioAbertoEDobra()
    {
        var time = new FakeTimeProvider();
        var breaker = new CircuitBreaker(time, Base, Max);

        breaker.RecordConnectionFailure();
        Assert.True(breaker.IsOpen);
        Assert.Equal(Base, breaker.Remaining);

        time.Advance(Base);
        Assert.False(breaker.IsOpen);
        Assert.True(breaker.IsHalfOpen); // the next cycle probes with one message

        breaker.RecordConnectionFailure(); // the probe failed: open twice as long
        Assert.Equal(2 * Base, breaker.Remaining);

        time.Advance(2 * Base);
        breaker.RecordSuccess(); // the probe went through
        Assert.False(breaker.IsOpen);
        Assert.False(breaker.IsHalfOpen);
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(2, 2)]
    [InlineData(5, 16)]
    [InlineData(6, 30)]
    [InlineData(40, 30)]
    public void Breaker_TempoAbertoDobraAteOTeto(int failures, int expectedSeconds) =>
        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), CircuitBreaker.Delay(Base, Max, failures));

    [Fact]
    public void Lote_PressaoReduzAMetadeAte1_SaudeDobraAteOConfigurado()
    {
        var sizer = new BatchSizer(100);

        sizer.OnPressure();
        Assert.Equal(50, sizer.Current);
        for (var i = 0; i < 10; i++)
            sizer.OnPressure();
        Assert.Equal(1, sizer.Current);

        sizer.OnHealthy();
        sizer.OnHealthy();
        Assert.Equal(4, sizer.Current);
        for (var i = 0; i < 10; i++)
            sizer.OnHealthy();
        Assert.Equal(100, sizer.Current);
    }
}
