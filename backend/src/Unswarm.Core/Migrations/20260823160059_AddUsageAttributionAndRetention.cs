using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Unswarm.Core.Migrations
{
    /// <inheritdoc />
    public partial class AddUsageAttributionAndRetention : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ApiKeyId",
                table: "UsageRecords",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ApiKeyName",
                table: "UsageRecords",
                type: "TEXT",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ProviderBudgetsJson",
                table: "Settings",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<int>(
                name: "UsageRetentionDays",
                table: "Settings",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.UpdateData(
                table: "Settings",
                keyColumn: "Id",
                keyValue: "default",
                columns: new[] { "ProviderBudgetsJson", "UsageRetentionDays" },
                values: new object[] { "{}", 30 });

            migrationBuilder.CreateIndex(
                name: "IX_UsageRecords_ApiKeyId",
                table: "UsageRecords",
                column: "ApiKeyId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_UsageRecords_ApiKeyId",
                table: "UsageRecords");

            migrationBuilder.DropColumn(
                name: "ApiKeyId",
                table: "UsageRecords");

            migrationBuilder.DropColumn(
                name: "ApiKeyName",
                table: "UsageRecords");

            migrationBuilder.DropColumn(
                name: "ProviderBudgetsJson",
                table: "Settings");

            migrationBuilder.DropColumn(
                name: "UsageRetentionDays",
                table: "Settings");
        }
    }
}
