using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Unswarm.Core.Migrations
{
    /// <inheritdoc />
    public partial class AgentKeyBinding : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "BoundAgentName",
                table: "ApiKeys",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "BoundAgentName",
                table: "ApiKeys");
        }
    }
}
