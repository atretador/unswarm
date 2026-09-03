using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Unswarm.Core.Migrations
{
    /// <inheritdoc />
    public partial class AddRouterRetrySettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "RouterRetryAttempts",
                table: "Settings",
                type: "INTEGER",
                nullable: false,
                defaultValue: 3);

            migrationBuilder.AddColumn<int>(
                name: "RouterRetryDelayMs",
                table: "Settings",
                type: "INTEGER",
                nullable: false,
                defaultValue: 1000);

            migrationBuilder.UpdateData(
                table: "Settings",
                keyColumn: "Id",
                keyValue: "default",
                column: "RouterRetryAttempts",
                value: 3);

            migrationBuilder.UpdateData(
                table: "Settings",
                keyColumn: "Id",
                keyValue: "default",
                column: "RouterRetryDelayMs",
                value: 1000);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "RouterRetryAttempts",
                table: "Settings");

            migrationBuilder.DropColumn(
                name: "RouterRetryDelayMs",
                table: "Settings");
        }
    }
}
