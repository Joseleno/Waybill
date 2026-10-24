using System.Diagnostics;
using System.Text;

namespace Waybill.Tests.Integration.Convivencia;

/// <summary>
/// Waybill 0.1.0-alpha in a child process (tests/Waybill.Tests.LegacyHost, restored from nuget.org): two versions of the
/// same assemblies do not load side by side in one process. Reads the child's stdout, one protocol line per event, and
/// refuses a child that did not load 0.1.0-alpha (a stale package in the NuGet cache would make every test vacuous).
/// </summary>
internal sealed class LegacyHost : IAsyncDisposable
{
    public const string Version = "0.1.0-alpha";

    private readonly Process _process;
    private readonly List<string> _lines = [];
    private readonly StringBuilder _stderr = new();
    private readonly SemaphoreSlim _arrived = new(0);
    private readonly Task _reader;
    private int _consumed; // lines before this index were already returned by WaitForAsync

    private LegacyHost(Process process)
    {
        _process = process;
        _process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null)
                lock (_stderr)
                    _stderr.AppendLine(e.Data);
        };
        _process.BeginErrorReadLine();
        _reader = Task.Run(ReadAsync);
    }

    /// <summary>Starts the child in <paramref name="mode"/> and waits for its version line.</summary>
    public static async Task<LegacyHost> StartAsync(string mode, params string[] arguments) =>
        await StartAsync(mode, arguments, environment: null);

    public static async Task<LegacyHost> StartAsync(string mode, string[] arguments, IReadOnlyDictionary<string, string>? environment)
    {
        // tests/Waybill.Tests.Integration/bin/<config>/<tfm>/ -> tests/Waybill.Tests.LegacyHost/bin/<config>/<tfm>/
        var here = new DirectoryInfo(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar));
        var dll = Path.Combine(here.Parent!.Parent!.Parent!.Parent!.FullName, "Waybill.Tests.LegacyHost", "bin", here.Parent.Name, here.Name,
            "Waybill.Tests.LegacyHost.dll");
        var start = new ProcessStartInfo("dotnet")
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        start.ArgumentList.Add(dll);
        start.ArgumentList.Add(mode);
        foreach (var argument in arguments)
            start.ArgumentList.Add(argument);
        foreach (var (name, value) in environment ?? new Dictionary<string, string>())
            start.Environment[name] = value;

        var host = new LegacyHost(Process.Start(start)!);
        var version = await host.WaitForAsync("VERSION ", TimeSpan.FromSeconds(60));
        if (!version.StartsWith(Version + "+", StringComparison.Ordinal))
        {
            await host.DisposeAsync();
            throw new InvalidOperationException($"the legacy host loaded Waybill {version}, not {Version}");
        }
        return host;
    }

    /// <summary>The payload of every line so far that starts with <paramref name="prefix"/>.</summary>
    public IReadOnlyList<string> Lines(string prefix)
    {
        lock (_lines)
            return _lines.Where(l => l.StartsWith(prefix, StringComparison.Ordinal)).Select(l => l[prefix.Length..]).ToList();
    }

    /// <summary>Waits for the next line, after the last one this returned, that starts with <paramref name="prefix"/>.</summary>
    public async Task<string> WaitForAsync(string prefix, TimeSpan timeout)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cts.CancelAfter(timeout);
        while (true)
        {
            lock (_lines)
            {
                for (var i = _consumed; i < _lines.Count; i++)
                {
                    if (_lines[i].StartsWith(prefix, StringComparison.Ordinal))
                    {
                        _consumed = i + 1;
                        return _lines[i][prefix.Length..];
                    }
                }
                if (_reader.IsCompleted)
                    throw Failure($"the legacy host exited before a \"{prefix}\" line");
            }
            try
            {
                await _arrived.WaitAsync(cts.Token);
            }
            catch (OperationCanceledException) when (!TestContext.Current.CancellationToken.IsCancellationRequested)
            {
                throw Failure($"no \"{prefix}\" line from the legacy host in {timeout}");
            }
        }
    }

    /// <summary>Waits until <paramref name="done"/> holds over the lines read so far.</summary>
    public async Task WaitUntilAsync(Func<LegacyHost, bool> done, TimeSpan timeout, string what)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cts.CancelAfter(timeout);
        while (!done(this))
        {
            if (_reader.IsCompleted)
                throw Failure($"the legacy host exited before {what}");
            try
            {
                await _arrived.WaitAsync(cts.Token);
            }
            catch (OperationCanceledException) when (!TestContext.Current.CancellationToken.IsCancellationRequested)
            {
                throw Failure($"not {what} after {timeout}");
            }
        }
    }

    /// <summary>Closes stdin, so a long-running mode stops gracefully, and waits for the child to exit with code 0.</summary>
    public async Task StopAsync()
    {
        _process.StandardInput.Close();
        await ExitedAsync();
    }

    /// <summary>Waits for a one-shot mode to end with code 0.</summary>
    public async Task ExitedAsync()
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(60));
        await _process.WaitForExitAsync(cts.Token);
        await _reader;
        if (_process.ExitCode != 0)
            throw Failure($"the legacy host exited with code {_process.ExitCode}");
    }

    private async Task ReadAsync()
    {
        while (await _process.StandardOutput.ReadLineAsync() is { } line)
        {
            lock (_lines)
                _lines.Add(line);
            _arrived.Release();
        }
        _arrived.Release();
    }

    private InvalidOperationException Failure(string what)
    {
        lock (_stderr)
            return new InvalidOperationException($"{what}. stderr:{Environment.NewLine}{_stderr}");
    }

    public async ValueTask DisposeAsync()
    {
        if (!_process.HasExited)
        {
            _process.Kill(entireProcessTree: true);
            await _process.WaitForExitAsync();
        }
        _process.Dispose();
        _arrived.Dispose();
    }
}
