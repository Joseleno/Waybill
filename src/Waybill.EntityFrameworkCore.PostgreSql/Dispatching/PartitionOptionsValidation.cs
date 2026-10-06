using Microsoft.Extensions.Options;

namespace Waybill.EntityFrameworkCore.Dispatching;

/// <summary>
/// Checks the partition options against <see cref="WaybillOptions.OrderByKey"/>: the partition lease only matters, and is
/// only enforced, when ordering is on, so raising <c>PublishTimeout</c> never breaks a dispatcher that does not order.
/// </summary>
internal sealed class PartitionOptionsValidation(IOptions<WaybillOptions> waybill) : IValidateOptions<WaybillDispatcherOptions>
{
    internal const int MaxPartitions = 1024;

    public ValidateOptionsResult Validate(string? name, WaybillDispatcherOptions options)
    {
        if (options.Partitions is < 1 or > MaxPartitions)
            return ValidateOptionsResult.Fail($"WaybillDispatcherOptions.Partitions must be from 1 to {MaxPartitions}.");

        // A claim needs the partition held for longer than the row lease it takes, measured from the claim itself.
        if (waybill.Value.OrderByKey && (options.PartitionLease < options.Lease * 2 || options.PartitionLease > TimeSpan.FromDays(1)))
            return ValidateOptionsResult.Fail(
                $"WaybillDispatcherOptions.PartitionLease must be at least twice the lease ({options.Lease * 2}: PublishTimeout plus LeaseMargin, doubled) and at most one day.");

        return ValidateOptionsResult.Success;
    }
}
