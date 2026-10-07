using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Waybill.EntityFrameworkCore.Schema.Migrations
{
    /// <inheritdoc />
    internal partial class SchemaV0_2 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "next_attempt_at",
                schema: "waybill",
                table: "outbox",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "outbox_instances",
                schema: "waybill",
                columns: table => new
                {
                    owner = table.Column<string>(type: "text", nullable: false),
                    heartbeat_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_outbox_instances", x => x.owner);
                });

            migrationBuilder.CreateTable(
                name: "outbox_partitions",
                schema: "waybill",
                columns: table => new
                {
                    partition = table.Column<int>(type: "integer", nullable: false),
                    owner = table.Column<string>(type: "text", nullable: true),
                    epoch = table.Column<long>(type: "bigint", nullable: false, defaultValue: 0L),
                    lease_until = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_outbox_partitions", x => x.partition);
                });

            migrationBuilder.CreateTable(
                name: "settings",
                schema: "waybill",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false),
                    partitions = table.Column<int>(type: "integer", nullable: false),
                    partition_lease = table.Column<TimeSpan>(type: "interval", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "clock_timestamp()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_settings", x => x.id);
                    table.CheckConstraint("ck_settings_single_row", "id = 1");
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "outbox_instances",
                schema: "waybill");

            migrationBuilder.DropTable(
                name: "outbox_partitions",
                schema: "waybill");

            migrationBuilder.DropTable(
                name: "settings",
                schema: "waybill");

            migrationBuilder.DropColumn(
                name: "next_attempt_at",
                schema: "waybill",
                table: "outbox");
        }
    }
}
