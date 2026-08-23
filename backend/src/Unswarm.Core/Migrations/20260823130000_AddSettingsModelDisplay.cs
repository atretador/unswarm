using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Unswarm.Core.Migrations
{
    /// <inheritdoc />
    public partial class AddSettingsModelDisplay : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AgentDisplayNames",
                table: "Settings",
                type: "TEXT",
                nullable: false,
                defaultValue: "{}");

            migrationBuilder.AddColumn<bool>(
                name: "HideOriginPrefix",
                table: "Settings",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.UpdateData(
                table: "Settings",
                keyColumn: "Id",
                keyValue: "default",
                columns: new[] { "AgentDisplayNames", "HideOriginPrefix" },
                values: new object[] { "{}", false });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AgentDisplayNames",
                table: "Settings");

            migrationBuilder.DropColumn(
                name: "HideOriginPrefix",
                table: "Settings");
        }
    }
}
