using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Waybill.EntityFrameworkCore.Schema.Migrations
{
    /// <inheritdoc />
    internal partial class InitialSchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "waybill");

            migrationBuilder.CreateTable(
                name: "inbox",
                schema: "waybill",
                columns: table => new
                {
                    handler = table.Column<string>(type: "text", nullable: false),
                    message_id = table.Column<Guid>(type: "uuid", nullable: false),
                    processed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "clock_timestamp()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_inbox", x => new { x.handler, x.message_id });
                });

            migrationBuilder.CreateTable(
                name: "outbox",
                schema: "waybill",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    type = table.Column<string>(type: "text", nullable: false),
                    key = table.Column<string>(type: "text", nullable: true),
                    key_hash = table.Column<int>(type: "integer", nullable: false),
                    sequence = table.Column<long>(type: "bigint", nullable: true),
                    payload = table.Column<byte[]>(type: "bytea", nullable: false),
                    content_type = table.Column<string>(type: "text", nullable: false),
                    headers = table.Column<string>(type: "jsonb", nullable: true),
                    status = table.Column<string>(type: "text", nullable: false, defaultValue: "pending"),
                    owner = table.Column<string>(type: "text", nullable: true),
                    fence = table.Column<long>(type: "bigint", nullable: false, defaultValue: 0L),
                    attempts = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    lease_until = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "clock_timestamp()"),
                    published_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    dlq_reason = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_outbox", x => x.id);
                    table.CheckConstraint("ck_outbox_status", "status IN ('pending', 'claimed', 'published', 'dlq')");
                });

            migrationBuilder.CreateIndex(
                name: "ix_outbox_claimable",
                schema: "waybill",
                table: "outbox",
                column: "id",
                filter: "status IN ('pending', 'claimed')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "inbox",
                schema: "waybill");

            migrationBuilder.DropTable(
                name: "outbox",
                schema: "waybill");
        }
    }
}
