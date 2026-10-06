using Waybill.EntityFrameworkCore.Dispatching;
using Waybill.EntityFrameworkCore.Retention;

namespace Waybill.Tests.Unit;

// The defaults table in docs/OPERATIONS.md (and the README) promises these values. Changing one here means changing
// the documents too.
public sealed class OpcoesDefaultsTests
{
    [Fact]
    public void Opcoes_Defaults_SaoOsDocumentadosNoOperations()
    {
        var dispatcher = new WaybillDispatcherOptions();
        var retention = new WaybillRetentionOptions();

        Assert.Equal(100, dispatcher.BatchSize);
        Assert.Equal(TimeSpan.FromSeconds(1), dispatcher.PollingInterval);
        Assert.Equal(TimeSpan.FromSeconds(20), dispatcher.PublishTimeout);
        Assert.Equal(TimeSpan.FromSeconds(10), dispatcher.LeaseMargin);
        Assert.Equal(TimeSpan.FromSeconds(30), dispatcher.Lease);
        Assert.Equal(5, dispatcher.MaxReturns);
        Assert.Equal(TimeSpan.FromMinutes(1), dispatcher.ReturnBackoff);
        Assert.Equal(TimeSpan.FromMinutes(10), dispatcher.MaxReturnBackoff);
        Assert.Equal(16, dispatcher.Partitions);
        Assert.Equal(TimeSpan.FromSeconds(60), dispatcher.PartitionLease);
        Assert.False(new WaybillOptions().OrderByKey);
        Assert.Equal(TimeSpan.FromSeconds(15), dispatcher.MetricsInterval);
        Assert.Equal(TimeSpan.FromDays(7), retention.OutboxRetention);
        Assert.Equal(TimeSpan.FromDays(30), retention.InboxRetention);
        Assert.Equal(TimeSpan.FromMinutes(5), retention.Interval);
        Assert.Equal(1000, retention.BatchSize);
    }
}
