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
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "next_attempt_at",
                schema: "waybill",
                table: "outbox");
        }
    }
}
