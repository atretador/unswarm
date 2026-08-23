using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Unswarm.Core.Migrations
{
    /// <inheritdoc />
    public partial class AddUsageRecordProviderKind : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ProviderKind",
                table: "UsageRecords",
                type: "TEXT",
                maxLength: 16,
                nullable: false,
                defaultValue: "local");

            // Backfill: historical rows recorded with the generic "cloud" bucket
            // are cloud-kind; everything else (generic "local" included) stays
            // local. Historical data cannot be retro-granular.
            migrationBuilder.Sql(
                "UPDATE UsageRecords SET ProviderKind = 'cloud' WHERE Provider = 'cloud';");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ProviderKind",
                table: "UsageRecords");
        }
    }
}
