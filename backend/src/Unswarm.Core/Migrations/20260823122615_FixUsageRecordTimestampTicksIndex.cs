using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Unswarm.Core.Migrations
{
    /// <inheritdoc />
    public partial class FixUsageRecordTimestampTicksIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_UsageRecords_Timestamp",
                table: "UsageRecords");

            migrationBuilder.CreateIndex(
                name: "IX_UsageRecords_TimestampTicks",
                table: "UsageRecords",
                column: "TimestampTicks");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_UsageRecords_TimestampTicks",
                table: "UsageRecords");

            migrationBuilder.CreateIndex(
                name: "IX_UsageRecords_Timestamp",
                table: "UsageRecords",
                column: "Timestamp");
        }
    }
}
