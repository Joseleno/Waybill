using Microsoft.Extensions.Options;

namespace Waybill.EntityFrameworkCore.Dispatching;

/// <summary>
/// Checks the partition options against <see cref="WaybillDispatcherOptions.OrderByKey"/>: the partition lease only matters, and is
/// only enforced, when ordering is on, so raising <c>PublishTimeout</c> never breaks a dispatcher that does not order.
/// </summary>
internal sealed class PartitionOptionsValidation : IValidateOptions<WaybillDispatcherOptions>
{
    internal const int MaxPartitions = 1024;

    public ValidateOptionsResult Validate(string? name, WaybillDispatcherOptions options)
    {
        if (options.Partitions is < 1 or > MaxPartitions)
            return ValidateOptionsResult.Fail($"WaybillDispatcherOptions.Partitions must be from 1 to {MaxPartitions}.");

        // Also the period of the settings check when ordering is off (ADR 0007): zero would read them every cycle.
        if (options.PartitionLease <= TimeSpan.Zero || options.PartitionLease > TimeSpan.FromDays(1))
            return ValidateOptionsResult.Fail("WaybillDispatcherOptions.PartitionLease must be positive and at most one day.");

        // A claim needs the partition held for longer than the row lease it takes, measured from the claim itself.
        if (options.OrderByKey && options.PartitionLease < options.Lease * 2)
            return ValidateOptionsResult.Fail(
                $"WaybillDispatcherOptions.PartitionLease must be at least twice the lease ({options.Lease * 2}: PublishTimeout plus LeaseMargin, doubled).");

        // Stored as a PostgreSQL interval and compared on every check: a fraction of a millisecond could come back
        // different and stop every dispatcher.
        if (options.OrderByKey && options.PartitionLease.Ticks % TimeSpan.TicksPerMillisecond != 0)
            return ValidateOptionsResult.Fail("WaybillDispatcherOptions.PartitionLease must be a whole number of milliseconds.");

        return ValidateOptionsResult.Success;
    }
}
