using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Unswarm.Core.Migrations
{
    /// <inheritdoc />
    public partial class OptimizeLogIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Logs_Timestamp",
                table: "Logs");

            migrationBuilder.CreateIndex(
                name: "IX_Logs_TimestampTicks",
                table: "Logs",
                column: "TimestampTicks");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Logs_TimestampTicks",
                table: "Logs");

            migrationBuilder.CreateIndex(
                name: "IX_Logs_Timestamp",
                table: "Logs",
                column: "Timestamp");
        }
    }
}
