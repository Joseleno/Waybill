using System.Collections.Concurrent;
using System.Diagnostics;

namespace Waybill.Tests; // shared: the unit test project links this file

/// <summary>
/// Records the spans of one test. An <see cref="ActivityListener"/> hears every source in the process, and test classes
/// run in parallel, so this one samples and records only what belongs to its test: the trace of its root, roots tagged
/// with one of its message ids, and roots with one of its names (a span another library opens without a parent). For
/// anything else its <c>Sample</c> says <see cref="ActivitySamplingResult.None"/>, never <c>PropagationData</c>, which
/// would make other tests' spans real. It imitates the SDK's default ParentBased sampler: a parent that is not recorded
/// samples nothing. It listens only to named sources, never to the sourceless activities Waybill uses to propagate.
/// </summary>
internal sealed class TraceRecorder : IDisposable
{
    private readonly HashSet<string> _sources;
    private readonly ConcurrentDictionary<string, byte> _messageIds = new();
    private readonly ConcurrentDictionary<string, byte> _rootNames = new();
    private readonly ConcurrentQueue<Activity> _stopped = new();
    private readonly ActivityListener _listener;

    /// <param name="sources">The sources to listen to, by name.</param>
    public TraceRecorder(params string[] sources)
        : this(recorded: true, sources)
    {
    }

    /// <param name="recorded">Whether the root is recorded; a root that is not stands for a request nobody samples.</param>
    /// <param name="sources">The sources to listen to, by name.</param>
    public TraceRecorder(bool recorded, params string[] sources)
    {
        _sources = new HashSet<string>(sources, StringComparer.Ordinal);
        // The root comes first: its trace id is what the listener filters on. Sourceless, so no listener samples it,
        // and recorded, as an incoming request a host instruments.
        Root = new Activity("test-root");
        Root.SetIdFormat(ActivityIdFormat.W3C);
        if (recorded)
            Root.ActivityTraceFlags = ActivityTraceFlags.Recorded;
        _listener = new ActivityListener
        {
            ShouldListenTo = source => _sources.Contains(source.Name),
            Sample = (ref ActivityCreationOptions<ActivityContext> options) => Accepts(options)
                ? ActivitySamplingResult.AllDataAndRecorded
                : ActivitySamplingResult.None,
            ActivityStopped = activity =>
            {
                if (activity.TraceId == TraceId || OwnsRoot(activity.ParentSpanId == default, activity.OperationName, activity.GetTagItem("messaging.message.id")))
                    _stopped.Enqueue(activity);
            },
        };
        ActivitySource.AddActivityListener(_listener);
    }

    /// <summary>The test's root, not started: <see cref="StartRoot"/> starts it.</summary>
    public Activity Root { get; }

    public ActivityTraceId TraceId => Root.TraceId;

    /// <summary>Spans of this test that ended, in the order they ended.</summary>
    public IReadOnlyList<Activity> Stopped => [.. _stopped];

    /// <summary>Starts the root as <see cref="Activity.Current"/>; stop it to leave the flow without one.</summary>
    public Activity StartRoot() => Root.Start();

    /// <summary>Also records root spans tagged at creation with this message id.</summary>
    public void OwnMessage(Guid messageId) => _messageIds.TryAdd(messageId.ToString(), 0);

    /// <summary>Also records root spans with this name (for example <c>publish {routing key}</c> of RabbitMQ.Client).</summary>
    public void OwnRootName(string name) => _rootNames.TryAdd(name, 0);

    private bool Accepts(in ActivityCreationOptions<ActivityContext> options)
    {
        if (options.Parent != default)
            return options.Parent.TraceId == TraceId && options.Parent.TraceFlags.HasFlag(ActivityTraceFlags.Recorded);
        object? messageId = null;
        foreach (var tag in options.Tags ?? [])
        {
            if (tag.Key == "messaging.message.id")
                messageId = tag.Value;
        }
        return OwnsRoot(true, options.Name, messageId);
    }

    private bool OwnsRoot(bool isRoot, string name, object? messageId) =>
        isRoot && (_rootNames.ContainsKey(name) || (messageId is string id && _messageIds.ContainsKey(id)));

    public void Dispose() => _listener.Dispose();
}
