using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Unswarm.Core.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddUsageRecordAgent : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Agent",
                table: "UsageRecords",
                type: "TEXT",
                maxLength: 128,
                nullable: true);

            // Backfill only unambiguous local rows: a usage record's persisted
            // Provider holds the raw serving-runtime display name, so resolve it
            // back to that runtime's Agent — but only when exactly one runtime
            // currently carries that display name. Ambiguous/renamed/unmatched
            // rows stay NULL and fall back to Provider at read time.
            migrationBuilder.Sql(
                """
                UPDATE UsageRecords
                SET Agent = (
                    SELECT r.Agent
                    FROM RegisteredContainers r
                    WHERE r.DisplayName = UsageRecords.Provider
                      AND r.Agent IS NOT NULL
                      AND TRIM(r.Agent) != ''
                )
                WHERE ProviderKind = 'local'
                  AND Agent IS NULL
                  AND (
                    SELECT COUNT(*)
                    FROM RegisteredContainers r
                    WHERE r.DisplayName = UsageRecords.Provider
                  ) = 1;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Agent",
                table: "UsageRecords");
        }
    }
}
