using System.Diagnostics;
using System.Globalization;
using Npgsql;
using Waybill.Tests.Integration;
using Waybill.Tests.Integration.G2;

namespace Waybill.Tests.Chaos;

// G2 with kill -9: a dispatcher process dies with its batch claimed and stuck in publish. Nothing is lost: the
// batch is claimed again after its lease (never before) and published; the rest of the backlog keeps flowing.
// (A process killed with the claim statement still open is covered by the stage 0 spike, Q2b: the claim is a single
// autocommit statement, and PostgreSQL rolls it back when the connection drops.)
[Collection(PostgresCollection.Name)]
public sealed class G2_KillDuranteAPublicacao_VoltaAposLease(PostgresFixture postgres)
{
    [Fact]
    public async Task G2_KillDuranteAPublicacao_VoltaAposLease_NuncaAntes()
    {
        var ct = TestContext.Current.CancellationToken;
        var database = await TestDatabase.CreateAsync(postgres);
        var ids = await DispatcherHarness.EnqueueAsync(database, 30);
        var lease = TimeSpan.FromSeconds(5);

        using var victim = StartHost(database.ConnectionString, batchSize: 10, publishTimeout: TimeSpan.FromSeconds(3), leaseMargin: TimeSpan.FromSeconds(2));
        await ReadLineStartingWith(victim, "PUBLISHING", TimeSpan.FromSeconds(60), ct);
        var claimedAt = Stopwatch.StartNew();
        victim.Kill(entireProcessTree: true);
        await victim.WaitForExitAsync(ct);
        var victimRows = await database.CountAsync("claimed");

        await using var dataSource = NpgsqlDataSource.Create(database.ConnectionString);
        var transport = new FakeTransport();
        var survivor = DispatcherHarness.Create(dataSource, transport,
            DispatcherHarness.Options(database, o => { o.PublishTimeout = TimeSpan.FromSeconds(3); o.LeaseMargin = TimeSpan.FromSeconds(2); }));

        // Right away: the rest of the backlog flows, the victim's batch is untouched.
        await survivor.RunOnceAsync(ct);
        Assert.Equal(20, transport.Received.Count);
        Assert.Equal(10, await database.CountAsync("claimed"));

        TimeSpan? retakenAfter = null;
        while (claimedAt.Elapsed < lease + TimeSpan.FromSeconds(10) && await database.CountAsync("published") < 30)
        {
            if (await survivor.RunOnceAsync(ct) > 0)
                retakenAfter ??= claimedAt.Elapsed;
            else
                await Task.Delay(100, ct);
        }

        Assert.Equal(10, victimRows);
        Assert.NotNull(retakenAfter);
        Assert.True(retakenAfter >= lease - TimeSpan.FromMilliseconds(500), $"taken back before the lease: {retakenAfter}");
        Assert.Equal(30, await database.CountAsync("published"));
        Assert.Equal(ids.Order(), transport.Received.Select(m => m.MessageId).Order());
        Assert.Equal(10, await database.ScalarAsync("SELECT count(*) FROM waybill.outbox WHERE fence = 2 AND attempts = 0"));
    }

    private static Process StartHost(string connectionString, int batchSize, TimeSpan publishTimeout, TimeSpan leaseMargin)
    {
        // tests/Waybill.Tests.Chaos/bin/<config>/<tfm>/ -> tests/Waybill.Tests.ChaosHost/bin/<config>/<tfm>/
        var here = new DirectoryInfo(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar));
        var tfm = here.Name;
        var configuration = here.Parent!.Name;
        var host = Path.Combine(here.Parent.Parent!.Parent!.Parent!.FullName, "Waybill.Tests.ChaosHost", "bin", configuration, tfm, "Waybill.Tests.ChaosHost.dll");

        var start = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var argument in new[]
                 {
                     host, connectionString, batchSize.ToString(CultureInfo.InvariantCulture),
                     ((int)publishTimeout.TotalMilliseconds).ToString(CultureInfo.InvariantCulture),
                     ((int)leaseMargin.TotalMilliseconds).ToString(CultureInfo.InvariantCulture),
                 })
            start.ArgumentList.Add(argument);
        return Process.Start(start)!;
    }

    private static async Task<string> ReadLineStartingWith(Process process, string prefix, TimeSpan timeout, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        while (true)
        {
            var line = await process.StandardOutput.ReadLineAsync(cts.Token)
                ?? throw new InvalidOperationException($"the host exited: {await process.StandardError.ReadToEndAsync(ct)}");
            if (line.StartsWith(prefix, StringComparison.Ordinal))
                return line;
        }
    }
}
