using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Unswarm.Core.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddContainerCreationSupport : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "CreationConfigJson",
                table: "RegisteredContainers",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "CreationMode",
                table: "RegisteredContainers",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "ErrorDetail",
                table: "RegisteredContainers",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ErrorLogs",
                table: "RegisteredContainers",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CreationConfigJson",
                table: "RegisteredContainers");

            migrationBuilder.DropColumn(
                name: "CreationMode",
                table: "RegisteredContainers");

            migrationBuilder.DropColumn(
                name: "ErrorDetail",
                table: "RegisteredContainers");

            migrationBuilder.DropColumn(
                name: "ErrorLogs",
                table: "RegisteredContainers");
        }
    }
}
