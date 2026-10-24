using System.Text;

namespace Waybill.Tests.Integration.Convivencia;

// The coexistence tests run Waybill 0.1.0-alpha from nuget.org in a child process. This one proves the child is what the
// others rely on: it loads 0.1.0-alpha, not a package left in the NuGet cache or the code in src/, and reads the 0.1
// claim from the package itself.
public sealed class Convivencia_LegacyHost
{
    [Fact]
    public async Task Convivencia_LegacyHost_CarregaA01DoNuGet()
    {
        await using var host = await LegacyHost.StartAsync("claim-sql");
        var sql = Encoding.UTF8.GetString(Convert.FromBase64String(await host.WaitForAsync("CLAIM-SQL ", TimeSpan.FromSeconds(30))));
        await host.ExitedAsync();

        Assert.Contains("FOR UPDATE SKIP LOCKED", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("is_head", sql, StringComparison.Ordinal); // the 0.2 claim has it
    }
}
