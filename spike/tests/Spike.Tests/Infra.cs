using System.Text;
using Npgsql;
using Spike.Core;
using Testcontainers.PostgreSql;

namespace Spike.Tests;

public sealed class PgFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:18-alpine")
        .WithCommand("-c", "track_commit_timestamp=on", "-c", "max_connections=300")
        .Build();

    public NpgsqlDataSource DataSource { get; private set; } = null!;
    public string ConnectionString { get; private set; } = null!;

    public async ValueTask InitializeAsync()
    {
        await _container.StartAsync();
        ConnectionString = new NpgsqlConnectionStringBuilder(_container.GetConnectionString())
        {
            MaxPoolSize = 200,
        }.ConnectionString;
        DataSource = NpgsqlDataSource.Create(ConnectionString);
    }

    public async ValueTask DisposeAsync()
    {
        await DataSource.DisposeAsync();
        await _container.DisposeAsync();
    }

    public async Task<long> ScalarAsync(string sql)
    {
        await using var cmd = DataSource.CreateCommand(sql);
        return Convert.ToInt64(await cmd.ExecuteScalarAsync());
    }

    public async Task ExecAsync(string sql)
    {
        await using var cmd = DataSource.CreateCommand(sql);
        await cmd.ExecuteNonQueryAsync();
    }
}

[CollectionDefinition("pg")]
public sealed class PgCollection : ICollectionFixture<PgFixture>;

public static class Report
{
    private static readonly Lock Gate = new();

    public static string Dir
    {
        get
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null && !dir.GetFiles("*.slnx").Any())
                dir = dir.Parent;
            var path = Path.Combine(dir!.FullName, "results");
            Directory.CreateDirectory(path);
            return path;
        }
    }

    public static void Append(ITestOutputHelper output, string file, string line)
    {
        output.WriteLine(line);
        lock (Gate)
            File.AppendAllText(Path.Combine(Dir, file), line + Environment.NewLine, Encoding.UTF8);
    }

    public static double Percentile(IEnumerable<double> values, double p)
    {
        var sorted = values.Order().ToArray();
        if (sorted.Length == 0) return 0;
        var idx = (int)Math.Ceiling(p / 100 * sorted.Length) - 1;
        return sorted[Math.Clamp(idx, 0, sorted.Length - 1)];
    }
}

public static class Run
{
    /// <summary>Roda N dispatchers até todas as linhas estarem publicadas (ou estourar o tempo).</summary>
    public static async Task<(TimeSpan Elapsed, Dispatcher[] Dispatchers, bool Completed)> UntilDrainedAsync(
        PgFixture pg, int instances, Func<int, DispatcherOptions> options, FakePublisher publisher,
        long expected, TimeSpan timeout, Func<CancellationToken, Task>? alongside = null)
    {
        var dispatchers = Enumerable.Range(0, instances)
            .Select(i => new Dispatcher(pg.DataSource, options(i)))
            .ToArray();

        using var cts = new CancellationTokenSource();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var tasks = dispatchers.Select(d => Task.Run(() => d.RunAsync(publisher, TimeSpan.FromMilliseconds(5), cts.Token))).ToList();
        if (alongside is not null)
            tasks.Add(Task.Run(() => alongside(cts.Token)));

        var completed = false;
        while (sw.Elapsed < timeout)
        {
            if (dispatchers.Sum(d => Interlocked.Read(ref d.MarkedTotal)) >= expected)
            {
                completed = true;
                break;
            }
            await Task.Delay(20);
        }
        var elapsed = sw.Elapsed;
        await cts.CancelAsync();
        await Task.WhenAll(tasks);
        foreach (var d in dispatchers)
            await d.ReleasePartitionsAsync();
        return (elapsed, dispatchers, completed);
    }
}
