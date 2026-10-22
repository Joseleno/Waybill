using System.Diagnostics;

namespace Waybill;

/// <summary>Waybill's activity source, and the way to open and close spans outside the caller's flow.</summary>
internal static class WaybillActivity
{
    public static readonly ActivitySource Source = new(
        WaybillDiagnostics.ActivitySourceName, typeof(WaybillActivity).Assembly.GetName().Version?.ToString());

    /// <summary>
    /// Opens a span with <paramref name="parent"/> as its parent, or as a root when it is <c>default</c>, without
    /// touching the caller's <see cref="Activity.Current"/>. Starting an activity makes it current, and saving and
    /// restoring Current by hand fails twice: the setter ignores a stopped activity, and a listener that throws leaves the
    /// half-started span current. An async method runs on its own execution context and gives the caller's back when it
    /// returns, also when it throws.
    /// </summary>
    /// <returns>The span, in W3C format whatever the process default; null when nothing listens or samples it.</returns>
    public static Activity? StartIsolated(
        string name, ActivityKind kind, ActivityContext parent, IEnumerable<KeyValuePair<string, object?>>? tags = null,
        IEnumerable<ActivityLink>? links = null)
    {
        if (!Source.HasListeners())
            return null;
        return Isolated().GetAwaiter().GetResult();

#pragma warning disable CS1998 // async for its execution context, not to await: it completes synchronously
        async ValueTask<Activity?> Isolated()
        {
            Activity.Current = null; // a default parent must make a root, never a child of whatever was current
            return Source.CreateActivity(name, kind, parent, tags, links, ActivityIdFormat.W3C)?.Start();
        }
#pragma warning restore CS1998
    }

    /// <summary>Stops a span opened by <see cref="StartIsolated"/>, without touching the caller's <see cref="Activity.Current"/>.</summary>
    public static void StopIsolated(Activity? activity)
    {
        if (activity is null)
            return;
        Isolated().GetAwaiter().GetResult();

#pragma warning disable CS1998 // as in StartIsolated
        async ValueTask Isolated() => activity.Stop(); // Stop sets Current back to what it was at Start
#pragma warning restore CS1998
    }
}
