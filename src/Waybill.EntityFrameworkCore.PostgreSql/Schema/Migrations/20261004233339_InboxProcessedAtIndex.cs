using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Waybill.EntityFrameworkCore.Schema.Migrations
{
    /// <inheritdoc />
    internal partial class InboxProcessedAtIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "ix_inbox_processed_at",
                schema: "waybill",
                table: "inbox",
                column: "processed_at");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_inbox_processed_at",
                schema: "waybill",
                table: "inbox");
        }
    }
}
