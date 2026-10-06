namespace Waybill.EntityFrameworkCore.Retention;

/// <summary>Configuration for retention cleanup. Set it up with <c>services.AddWaybillRetention(options => ...)</c>.</summary>
public sealed class WaybillRetentionOptions
{
    /// <summary>PostgreSQL connection string of the database that holds the <c>waybill</c> schema. Required.</summary>
    public string? ConnectionString { get; set; }

    /// <summary>
    /// How long a published outbox row is kept, counted from its publication. Pending, claimed and dead-lettered rows
    /// are never deleted. Default 7 days.
    /// </summary>
    public TimeSpan OutboxRetention { get; set; } = TimeSpan.FromDays(7);

    /// <summary>
    /// How long an inbox row is kept, counted from its processing. A message delivered again after its row is deleted
    /// is processed again, so keep this above the longest delay between enqueueing and the last redelivery: outbox
    /// backlog, broker queue and DLQ reprocessing window. Default 30 days.
    /// </summary>
    public TimeSpan InboxRetention { get; set; } = TimeSpan.FromDays(30);

    /// <summary>Wait between cleanup passes. Default 5 minutes.</summary>
    public TimeSpan Interval { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>Maximum rows deleted per statement; a pass repeats until a batch comes back short. Default 1000.</summary>
    public int BatchSize { get; set; } = 1000;
}
