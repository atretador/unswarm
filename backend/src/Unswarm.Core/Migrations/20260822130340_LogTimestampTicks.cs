using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Unswarm.Core.Migrations
{
    /// <inheritdoc />
    public partial class LogTimestampTicks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "TimestampTicks",
                table: "Logs",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0L);

            // Backfill existing rows from the TEXT Timestamp column (SQLite stores
            // DateTimeOffset as ISO-8601 text; julianday parses it).
            migrationBuilder.Sql(
                "UPDATE \"Logs\" SET \"TimestampTicks\" = CAST((julianday(\"Timestamp\") - 2440587.5) * 864000000000 AS INTEGER);");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "TimestampTicks",
                table: "Logs");
        }
    }
}
