using Microsoft.EntityFrameworkCore;
using Waybill.EntityFrameworkCore.Schema;

namespace Waybill.EntityFrameworkCore;

/// <summary>Creates and upgrades Waybill's tables (schema <c>waybill</c>) with the migrations shipped in this package.</summary>
public static class WaybillSchema
{
    internal const string Name = "waybill";
    internal const string OutboxTable = "outbox";
    internal const string InboxTable = "inbox";
    internal const string SettingsTable = "settings";
    internal const string PartitionsTable = "outbox_partitions";
    internal const string InstancesTable = "outbox_instances";
    internal const string MigrationsHistoryTable = "__waybill_migrations";

    /// <summary>
    /// Applies the pending migrations of the Waybill schema. Run it from an initialization step (a migration job or
    /// an init container), not from every API instance at startup. The application's own tables and migrations are
    /// not touched; Waybill keeps its history in <c>waybill.__waybill_migrations</c>.
    /// </summary>
    public static async Task MigrateAsync(string connectionString, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        await using var context = new WaybillSchemaContext(WaybillSchemaContext.Options(connectionString));
        await context.Database.MigrateAsync(cancellationToken).ConfigureAwait(false);
    }
}
