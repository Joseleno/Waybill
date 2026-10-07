namespace Waybill.EntityFrameworkCore.Dispatching;

/// <summary>Configuration for the outbox dispatcher. Set it up with <c>services.AddWaybillDispatcher(options => ...)</c>.</summary>
public sealed class WaybillDispatcherOptions
{
    /// <summary>PostgreSQL connection string of the database that holds the <c>waybill</c> schema. Required.</summary>
    public string? ConnectionString { get; set; }

    /// <summary>Maximum messages claimed and published per cycle. Default 100.</summary>
    public int BatchSize { get; set; } = 100;

    /// <summary>Wait before the next cycle when the last one did not fill a batch. Default 1 second.</summary>
    public TimeSpan PollingInterval { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>
    /// How long the dispatcher waits for the broker to confirm a batch. Past it, the outcome is unknown and the batch is
    /// handed back to be published again (a duplicate is possible, a loss is not). Default 20 seconds.
    /// </summary>
    public TimeSpan PublishTimeout { get; set; } = TimeSpan.FromSeconds(20);

    /// <summary>
    /// Safety margin between the end of the publish wait and the end of the lease, which absorbs the difference between
    /// the local clock (publish timeout) and the database clock (lease). Default 10 seconds.
    /// </summary>
    public TimeSpan LeaseMargin { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>How many times a message may be returned as unroutable before it goes to the DLQ. Default 5.</summary>
    public int MaxReturns { get; set; } = 5;

    /// <summary>
    /// How long a message returned as unroutable waits before it is published again; the wait doubles with each return,
    /// up to <see cref="MaxReturnBackoff"/>. With the defaults, a message spends 15 minutes waiting before it goes to
    /// the DLQ, time to create the missing binding. Zero publishes it again on the next cycle. Default 1 minute.
    /// </summary>
    public TimeSpan ReturnBackoff { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>The longest single wait after a return (see <see cref="ReturnBackoff"/>). Default 10 minutes.</summary>
    public TimeSpan MaxReturnBackoff { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>
    /// With <c>OrderByKey</c>: how many partitions (P) the keys are spread over, as <c>key_hash % P</c>. Each partition is
    /// held by one dispatcher at a time; more dispatchers than P leave the extra ones idle for keyed messages. Every
    /// dispatcher must use the same value. Default 16, from 1 to 1024.
    /// </summary>
    public int Partitions { get; set; } = 16;

    /// <summary>
    /// With <c>OrderByKey</c>: how long a dispatcher holds a partition without renewing it. A crashed dispatcher's keys
    /// move to another one after this. At least twice <see cref="Lease"/> and at most one day; every dispatcher must use
    /// the same value. Default 60 seconds.
    /// </summary>
    public TimeSpan PartitionLease { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>
    /// How often the age of the oldest message not yet published is sampled for the
    /// <c>waybill.outbox.oldest_pending.age</c> gauge (meter <c>Waybill</c>). Default 15 seconds.
    /// </summary>
    public TimeSpan MetricsInterval { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>The claim lease: <see cref="PublishTimeout"/> plus <see cref="LeaseMargin"/>, so it always outlives the publish wait.</summary>
    public TimeSpan Lease => PublishTimeout + LeaseMargin;
}
