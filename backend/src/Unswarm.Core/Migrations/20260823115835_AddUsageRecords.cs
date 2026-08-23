using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Unswarm.Core.Migrations
{
    /// <inheritdoc />
    public partial class AddUsageRecords : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "UsageRecords",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    Timestamp = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    TimestampTicks = table.Column<long>(type: "INTEGER", nullable: false),
                    Provider = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    Model = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    PromptTokens = table.Column<int>(type: "INTEGER", nullable: false),
                    CompletionTokens = table.Column<int>(type: "INTEGER", nullable: false),
                    CachedTokens = table.Column<int>(type: "INTEGER", nullable: false),
                    IsStreaming = table.Column<bool>(type: "INTEGER", nullable: false),
                    ElapsedMs = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UsageRecords", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_UsageRecords_Provider_Model",
                table: "UsageRecords",
                columns: new[] { "Provider", "Model" });

            migrationBuilder.CreateIndex(
                name: "IX_UsageRecords_Timestamp",
                table: "UsageRecords",
                column: "Timestamp");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "UsageRecords");
        }
    }
}
