using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Unswarm.Core.Migrations
{
    /// <inheritdoc />
    public partial class RenameActiveEntryToActiveModelId : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ActiveEntryIndex",
                table: "RouterProfiles");

            migrationBuilder.AddColumn<string>(
                name: "ActiveModelId",
                table: "RouterProfiles",
                type: "TEXT",
                maxLength: 256,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ActiveModelId",
                table: "RouterProfiles");

            migrationBuilder.AddColumn<int>(
                name: "ActiveEntryIndex",
                table: "RouterProfiles",
                type: "INTEGER",
                nullable: true);
        }
    }
}
