using Waybill.EntityFrameworkCore.Dispatching;

namespace Waybill.Tests.Unit;

// JIT is turned off by the dispatcher's own sessions, not through the startup parameter "options": a pooler such as
// PgBouncer refuses a startup parameter it does not track, and the dispatcher would not connect at all.
public sealed class DispatcherDataSourceTests
{
    [Fact]
    public void DispatcherDataSource_SemParametroDeStartupNovo()
    {
        using var dataSource = DispatcherDataSource.Create("Host=localhost;Username=app");

        Assert.DoesNotContain("jit", dataSource.Value.ConnectionString, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Options", dataSource.Value.ConnectionString, StringComparison.OrdinalIgnoreCase);
    }
}
